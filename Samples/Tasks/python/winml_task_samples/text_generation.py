# Copyright (C) Microsoft Corporation. All rights reserved.

"""Build and run Text Generation Tasks for the Python samples."""

from __future__ import annotations

from collections.abc import Callable
from contextlib import ExitStack
from pathlib import Path

from windowsml.runtime import (
    ChatCompletionMessage,
    ChatCompletionRequest,
    ChatRole,
    ExecutionTargetKind,
    Runtime,
    Tokenizer,
)
from windowsml.tasks import (
    Tasks,
    TextGenerationOptions,
    TextGenerationOutputKind,
    TextGenerationResult,
    TextGenerationSpeculativeMethod,
    TextGenerationTask,
)


# Capacity used only when a model does not declare one. The prepared Qwen export
# is built with this many positions.
DEFAULT_SEQUENCE_CAPACITY = 128

# Draft tokens proposed per step when the caller does not choose.
DEFAULT_DRAFT_TOKEN_COUNT = 3

SPECULATIVE_METHODS = {
    "none": TextGenerationSpeculativeMethod.NONE,
    "model": TextGenerationSpeculativeMethod.MODEL,
    "draft-model": TextGenerationSpeculativeMethod.DRAFT_MODEL,
    "prompt-lookup": TextGenerationSpeculativeMethod.PROMPT_LOOKUP,
}

# A schema reports this for a dimension the exporter left free. It is unsigned,
# so the non-positive check below will not catch it.
_FREE_DIMENSION = (1 << 64) - 1


def _read_declared_sequence_capacity(model) -> int:
    """Read sequence capacity declared by the model's packed state input."""

    try:
        descriptor = model.get_input_tensor_desc(1)
    except Exception:
        return DEFAULT_SEQUENCE_CAPACITY
    dimensions = getattr(descriptor, "dimensions", None)
    if not dimensions or len(dimensions) != 4:
        return DEFAULT_SEQUENCE_CAPACITY
    declared = int(dimensions[2])
    if declared <= 0 or declared == _FREE_DIMENSION:
        return DEFAULT_SEQUENCE_CAPACITY
    return declared


def open_text_generation_task(
    resources: ExitStack,
    model_path: Path,
    tokenizer_source: Path,
    backend: str = "auto",
    *,
    device: str = "cpu",
    speculative: str = "none",
    draft_tokens: int = 0,
    draft_model: Path | None = None,
) -> tuple[Tasks, Tokenizer, TextGenerationTask]:
    """Build one Runtime pipeline and compose a Text Generation Task over it.

    Returns the Tasks bootstrap, the tokenizer, and the task. Every object is
    entered into ``resources`` and closes with it.

    ``speculative`` proposes several tokens per step and verifies them in one
    pass: ``model`` uses the model's own draft predictor, either its built-in
    multi-token prediction (MTP/NextN) layers or a companion block draft
    (DFlash, DFlash2, or DSpark) passed as ``draft_model``; ``draft-model`` a
    smaller GGUF model sharing the tokenizer, passed as ``draft_model``; and
    ``prompt-lookup`` n-grams of earlier text. The sampled output distribution is
    unchanged.
    """

    extension = model_path.suffix.casefold()
    if extension not in {".onnx", ".ort", ".gguf"}:
        raise ValueError(
            "The Python Task sample requires an ONNX, ORT, or GGUF model."
        )
    if not model_path.is_file():
        raise FileNotFoundError(f"Language model was not found: {model_path}")
    if not tokenizer_source.exists():
        raise FileNotFoundError(f"Tokenizer source was not found: {tokenizer_source}")

    resolved_backend = backend.casefold()
    if resolved_backend == "auto":
        resolved_backend = "llama" if extension == ".gguf" else "ort"
    if resolved_backend not in {"ort", "llama"}:
        raise ValueError("backend must be auto, ort, or llama")
    if resolved_backend == "llama" and extension != ".gguf":
        raise ValueError("The llama backend requires a GGUF model.")
    if resolved_backend == "ort" and extension not in {".onnx", ".ort"}:
        raise ValueError("The ORT backend requires an ONNX or ORT model.")
    method = SPECULATIVE_METHODS.get(speculative.casefold())
    if method is None:
        raise ValueError("speculative must be none, model, draft-model, or prompt-lookup")
    # For the model method, draft_model names a companion block draft.
    block_draft = method == TextGenerationSpeculativeMethod.MODEL and draft_model is not None
    if method != TextGenerationSpeculativeMethod.NONE and resolved_backend != "llama":
        raise ValueError("The sample demonstrates speculative decoding with GGUF models.")
    if (method == TextGenerationSpeculativeMethod.DRAFT_MODEL or block_draft) != (
        draft_model is not None
    ):
        raise ValueError(
            "draft_model is required by draft-model, optional with model, and not used otherwise."
        )
    if draft_model is not None and not draft_model.is_file():
        raise FileNotFoundError(f"Draft model was not found: {draft_model}")
    if device not in {"cpu", "gpu"}:
        raise ValueError("device must be cpu or gpu")
    if device == "gpu" and resolved_backend != "llama":
        raise ValueError("device applies to the llama backend.")
    draft_token_count = 0
    if method != TextGenerationSpeculativeMethod.NONE:
        draft_token_count = draft_tokens or DEFAULT_DRAFT_TOKEN_COUNT

    # The Runtime creates every object supplied to the typed Task configuration.
    runtime = resources.enter_context(Runtime())
    target = resources.enter_context(
        runtime.create_target(ExecutionTargetKind.GPU)
        if device == "gpu"
        else runtime.create_cpu_target()
    )
    model = resources.enter_context(runtime.load_model(str(model_path)))
    # Build one Runtime model stage; the Task API consumes the resulting
    # pipeline and stage endpoints.
    builder = resources.enter_context(runtime.create_pipeline_builder())
    stage = resources.enter_context(
        builder.add_model_stage(model, target, "python-task-text-generation")
    )

    if resolved_backend != "llama":
        # The model declares its own capacity in the packed state it binds,
        # so read it rather than assuming one.
        stage.add_state_tensor_pair(1, 1)
        sequence_capacity = _read_declared_sequence_capacity(model)
        stage.set_sequence_capacity_hint(sequence_capacity)
        stage.request_output(0)
    elif draft_token_count:
        # Rolling back rejected draft tokens needs room reserved before Build,
        # and the model draft predictor (built-in layers or a companion block
        # draft) loads only on request.
        stage.draft_token_limit_hint = draft_token_count
        if method == TextGenerationSpeculativeMethod.MODEL:
            stage.model_draft_predictor_enabled = True
            if block_draft:
                stage.draft_predictor_path = str(draft_model)

    pipeline = resources.enter_context(builder.build())
    if resolved_backend == "ort":
        # ORT exposes mutable state capacity after Build.
        stage.set_sequence_capacity(sequence_capacity)
    resolved_tokenizer = (
        model_path if resolved_backend == "llama" else tokenizer_source
    )
    tokenizer = resources.enter_context(Tokenizer.from_file(str(resolved_tokenizer)))
    # IWinMLTasks is the bootstrap for typed Task configuration and sessions.
    tasks = resources.enter_context(Tasks(runtime))
    configuration = resources.enter_context(
        tasks.create_text_generation_configuration()
    )
    configuration.configure_unified(
        pipeline=pipeline,
        token_input_stage=stage,
        token_input_index=0,
        output_stage=stage,
        output_index=0,
        output_kind=TextGenerationOutputKind.LOGITS,
        state_owner_stage=stage,
        token_target=target,
        tokenizer=tokenizer,
    )
    if method != TextGenerationSpeculativeMethod.NONE:
        configuration.set_speculative_decoding(method, draft_token_count)
    if draft_model is not None and not block_draft:
        draft = resources.enter_context(runtime.load_model(str(draft_model)))
        draft_builder = resources.enter_context(runtime.create_pipeline_builder())
        draft_stage = resources.enter_context(
            draft_builder.add_model_stage(draft, target, "python-task-text-generation-draft")
        )
        draft_pipeline = resources.enter_context(draft_builder.build())
        configuration.set_draft_pipeline(
            pipeline=draft_pipeline,
            token_input_stage=draft_stage,
            token_input_index=0,
            output_stage=draft_stage,
            output_index=0,
            state_owner_stage=draft_stage,
        )
    configuration.validate()

    task = resources.enter_context(
        tasks.create_text_generation_task(
            tokenizer,
            default_configuration=configuration,
        )
    )
    return tasks, tokenizer, task


def generate_text(
    model_path: Path,
    tokenizer_source: Path,
    prompt: str,
    max_new_tokens: int,
    on_fragment: Callable[[str], None] | None = None,
    backend: str = "auto",
    chat: bool = False,
    *,
    device: str = "cpu",
    speculative: str = "none",
    draft_tokens: int = 0,
    draft_model: Path | None = None,
) -> TextGenerationResult:
    """Build a Text Generation Task and generate text.

    With ``chat`` set, the prompt is sent as a user turn in the model's chat
    template, so an instruct model ends its reply on its own. The speculative
    arguments are described on ``open_text_generation_task``.
    """

    with ExitStack() as resources:
        _, tokenizer, task = open_text_generation_task(
            resources,
            model_path,
            tokenizer_source,
            backend,
            device=device,
            speculative=speculative,
            draft_tokens=draft_tokens,
            draft_model=draft_model,
        )
        # Generation returns a pull stream; the terminal result remains available
        # after iteration completes and before the stream is closed.
        options = TextGenerationOptions(max_new_tokens=max_new_tokens)
        session = task.default_session
        if chat:
            # The structured conversation formatter renders the model's own chat
            # template, and generation starts from the formatted prompt tokens.
            request = ChatCompletionRequest(
                messages=(ChatCompletionMessage(role=ChatRole.USER, content=prompt),),
            )
            formatted = resources.enter_context(tokenizer.format_conversation(request))
            stream = resources.enter_context(
                session.generate_tokens(list(formatted.token_ids), options)
            )
        else:
            stream = resources.enter_context(session.generate_text(prompt, options))
        for update in stream:
            if on_fragment is not None:
                on_fragment(update.fragment)
        result = stream.result
        result.raise_for_error()
        return result

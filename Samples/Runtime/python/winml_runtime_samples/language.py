# Copyright (C) Microsoft Corporation. All rights reserved.

"""Language-model helpers built with windowsml.runtime primitives.

What this module wraps
  - Unified loading for ONNX/ORT and GGUF artifacts through Runtime.load_model.
  - Pipeline construction with one decoder stage or a split embedding/decoder/head
    ONNX pipeline.
  - Runtime-managed sequence state, tokenizer/chat-template formatting, and
    incremental token decoding.
  - Public ORT diagnostics when the selected stage exposes them.

Used by
  - language/hello-language-model/main.py
  - language/llm-chat/main.py
  - language/speech-to-language-model/main.py

Learn more
  ../../../../docs/Runtime/tutorials/03-language-models.md
  ../../../../docs/Runtime/tutorials/04-speech-to-language.md
  ../../../../docs/api-reference/CommonPatterns.md
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
import importlib
from pathlib import Path
import struct
from time import perf_counter
from typing import Any, Callable, Iterable, Sequence


class RuntimeCapabilityError(RuntimeError):
    """The installed Runtime does not support a feature required by the sample."""


# A stage descriptor reports UINT64_MAX for a dimension the backend left free.
# Treat that (and a zero) as "size not known", never as a real extent.
_DYNAMIC_DIMENSION = 0xFFFFFFFFFFFFFFFF


class ArtifactKind(str, Enum):
    ONNX = "onnx"
    GGUF = "gguf"


class Backend(str, Enum):
    ORT = "ort"
    LLAMA = "llama"


@dataclass(frozen=True)
class LanguageRequest:
    """The backend-neutral portion of a language-model launch request."""

    backend: Backend
    device: str = "cpu"
    ep: str | None = None
    tokenizer_source: Path | None = None
    context_capacity: int = 0
    target_policy: str | None = None


@dataclass(frozen=True)
class Generation:
    """Text and lightweight client-side timing collected from one token stream."""

    text: str
    token_count: int
    time_to_first_token_seconds: float | None
    total_seconds: float
    stop_reason: str | None

    @property
    def tokens_per_second(self) -> float:
        return self.token_count / self.total_seconds if self.total_seconds else 0.0


def _runtime_api() -> Any:
    """Import windowsml.runtime only when a sample starts execution."""

    try:
        return importlib.import_module("windowsml.runtime")
    except ImportError as error:
        raise RuntimeError(
            "This sample requires the matching windowsml package with "
            "windowsml.runtime."
        ) from error


def language_artifact_kind(path: str | Path) -> ArtifactKind:
    """Classify a supported unified language artifact without inspecting its model graph."""

    extension = Path(path).suffix.casefold()
    if extension in {".onnx", ".ort"}:
        return ArtifactKind.ONNX
    if extension == ".gguf":
        return ArtifactKind.GGUF
    raise ValueError(
        f"Unsupported language artifact '{path}'. Expected an ONNX, ORT, or GGUF file."
    )


def default_unified_model(runtime_root: Path, backend: Backend) -> Path:
    """Return the sample's default unified artifact for one backend."""

    if backend is Backend.LLAMA:
        return runtime_root / "models" / "gguf" / "qwen2.5-0.5b-instruct-q4_k_m.gguf"
    return runtime_root / "models" / "llm" / "model.onnx"


def _close_all(
    resources: Iterable[Any],
    *,
    primary_error: Exception | None = None,
) -> None:
    first_error: Exception | None = None
    for resource in resources:
        close = getattr(resource, "close", None)
        if close is not None:
            try:
                close()
            except Exception as error:
                if first_error is None:
                    first_error = error
    if first_error is not None:
        if primary_error is not None:
            primary_error.add_note(f"Resource cleanup also failed: {first_error!r}")
        else:
            raise first_error


def _fixed_size(dimension: int) -> int | None:
    """Return a concrete dimension, or None when the backend left it free."""

    if dimension in (0, _DYNAMIC_DIMENSION):
        return None
    return int(dimension)


def _ort_diagnostics(
    runtime_api: Any,
    stage: Any,
) -> tuple[str | None, str | None, bool] | None:
    """Read public ORT stage diagnostics, or None for a non-ORT stage.

    Both the diagnostics capability itself and each of its properties raise
    NotSupportedError when the stage is not ORT-backed, so the whole read is
    guarded; any other failure is a real error and propagates.
    """

    diagnostics_factory = getattr(stage, "ort_diagnostics", None)
    if diagnostics_factory is None:
        return None
    diagnostics = None
    try:
        diagnostics = diagnostics_factory()
        return (
            diagnostics.requested_provider,
            diagnostics.selected_provider,
            diagnostics.is_provider_pinned,
        )
    except runtime_api.NotSupportedError:
        return None
    finally:
        if diagnostics is not None:
            diagnostics.close()


def _resolved_target_name(stage: Any) -> str | None:
    """Report the hardware class a stage resolved to, closing the wrapper."""

    target = getattr(stage, "execution_target", None)
    if target is None:
        return None
    try:
        return getattr(getattr(target, "kind", None), "name", None)
    finally:
        close = getattr(target, "close", None)
        if close is not None:
            close()


def _declare_trailing_state_pairs(
    runtime_api: Any, model: Any, stage: Any
) -> tuple[int, int]:
    """Declare the unified decoder's key/value pairs before Build.

    The Runtime never infers persistent state, so the cache tensors must be
    declared. A unified export lays them out as trailing tensors: each
    ``past_*`` input lines up with the ``present_*`` output at the same offset
    from the end. Walk back while the declared descriptors agree, as the C++
    loader does. A model without an ONNX schema, such as GGUF, owns its state
    and declares nothing.

    Returns the pair count and the sequence capacity a fixed-size cache
    declares on the sequence axis of a rank-4 state tensor, or zero.
    """

    try:
        schema = model.schema()
    except runtime_api.WinMLError:
        return 0, 0
    with schema:
        try:
            input_count = schema.input_count
            output_count = schema.output_count
        except runtime_api.WinMLError:
            return 0, 0
        if input_count < 2 or output_count < 2:
            return 0, 0
        matched = 0
        while matched < min(input_count - 1, output_count - 1):
            if schema.input_desc(input_count - 1 - matched) != schema.output_desc(
                output_count - 1 - matched
            ):
                break
            matched += 1
        declared_capacity = 0
        if matched:
            _, shape = schema.input_desc(input_count - matched)
            if len(shape) == 4 and 0 < shape[2] < _DYNAMIC_DIMENSION:
                declared_capacity = int(shape[2])
    for pair in range(matched):
        stage.add_state_tensor_pair(
            input_count - matched + pair,
            output_count - matched + pair,
        )
    return matched, declared_capacity


def _probe_sequence_capacity(runtime_api: Any, stage: Any, label: str) -> int:
    """Cache the stage's fixed sequence capacity at construction time.

    A model that exposes no autoregressive sequence state cannot drive this
    loop at all, so it fails here with a model requirement instead of midway
    through the first response.
    """

    try:
        return stage.sequence_capacity
    except runtime_api.NotSupportedError as error:
        raise ValueError(
            f"the {label} does not expose Runtime sequence state; this sample "
            "requires an autoregressive decoder whose stage reports a sequence "
            "position and capacity"
        ) from error


def _last_token_argmax(logits: Any) -> int:
    if logits.ndim == 0 or logits.shape[-1] <= 0:
        raise ValueError("language-model logits must have a vocabulary dimension")
    vocabulary_size = logits.shape[-1]
    return int(logits.reshape(-1, vocabulary_size)[-1].argmax())


def _validate_request(request: LanguageRequest, artifact: ArtifactKind) -> None:
    if request.context_capacity < 0:
        raise ValueError("context capacity must not be negative")
    if request.target_policy not in {None, "performance", "efficiency"}:
        raise ValueError("target policy must be performance or efficiency")
    if request.backend is Backend.LLAMA and artifact is not ArtifactKind.GGUF:
        raise ValueError("--backend llama requires a GGUF artifact.")
    if request.backend is Backend.ORT and artifact is not ArtifactKind.ONNX:
        raise ValueError("--backend ort requires an ONNX or ORT artifact.")
    if artifact is ArtifactKind.GGUF and request.ep:
        raise ValueError("--ep selects an ORT provider and cannot be used with GGUF.")
    if request.ep and request.target_policy:
        raise ValueError(
            "--performance and --efficiency select generic Runtime targets and "
            "cannot be combined with --ep."
        )
    if artifact is ArtifactKind.GGUF and request.device.casefold() == "npu":
        raise ValueError("GGUF does not support the NPU target.")


def _target_kind(runtime_api: Any, device: str) -> Any:
    try:
        return getattr(runtime_api.ExecutionTargetKind, device.upper())
    except AttributeError as error:
        raise ValueError("--device must be cpu, gpu, or npu.") from error


def _target_preference(runtime_api: Any, policy: str | None) -> Any:
    preferences = getattr(runtime_api, "ExecutionTargetPreference", None)
    if preferences is None:
        raise RuntimeCapabilityError(
            "The installed windowsml package does not expose the generic "
            "execution-target factory required by this sample. Install the "
            "matching windowsml package."
        )
    if policy is None:
        return preferences.DEFAULT
    return getattr(preferences, policy.upper())


# Creates the stage target and, when --ep pins an ORT provider, keeps the
# underlying hardware target for tensor creation.
def _create_target(
    runtime_api: Any,
    runtime: Any,
    request: LanguageRequest,
) -> tuple[Any, Any]:
    """Create the stage target and its underlying hardware target.

    ``--ep`` pins a named ORT provider onto the hardware target the same way
    the non-language samples do, so the provider and the requested device class
    are selected by one explicit request.
    """

    if not request.ep:
        hardware_target = _create_hardware_target(
            runtime_api,
            runtime,
            request.device,
            request.target_policy,
        )
        return hardware_target, hardware_target
    # No hardware target: the Runtime pairs the pinned provider with one of its
    # own devices. On a machine with two GPUs, the default GPU may not be one
    # the provider supports. Host data goes into CPU tensors, and the Runtime
    # moves it to the stage's device.
    stage_target = runtime.create_ort_execution_target(
        request.ep,
        _target_kind(runtime_api, request.device.casefold()),
        None,
    )
    print(f"Pinned execution provider: {request.ep} ({request.device.upper()})")
    return stage_target, _cpu_tensor_target(runtime, stage_target)


def _cpu_tensor_target(runtime: Any, stage_target: Any) -> Any:
    try:
        return runtime.create_cpu_target()
    except Exception as error:
        _close_all((stage_target,), primary_error=error)
        raise


def _create_hardware_target(
    runtime_api: Any,
    runtime: Any,
    device: str,
    target_policy: str | None,
) -> Any:
    if device.casefold() == "cpu" and not target_policy:
        return runtime.create_cpu_target()
    return runtime.create_target(
        _target_kind(runtime_api, device.casefold()),
        _target_preference(runtime_api, target_policy),
    )


# Owns a built one-stage Runtime pipeline plus tokenizer state for manual text
# generation over a unified ONNX/ORT or GGUF artifact.
class UnifiedLanguageRunner:
    """Owns a unified primitive pipeline and caller-driven greedy generation."""

    def __init__(
        self,
        runtime_api: Any,
        runtime: Any,
        pipeline: Any,
        stage: Any,
        tokenizer: Any,
        decoder: Any,
        token_target: Any,
        token_data_type: Any,
        token_rank: int,
        vocabulary_size: int | None,
        end_tokens: frozenset[int],
        sequence_capacity: int,
        resources: Sequence[Any],
    ) -> None:
        self._runtime_api = runtime_api
        self._runtime = runtime
        self._pipeline = pipeline
        self._stage = stage
        self._tokenizer = tokenizer
        self._decoder = decoder
        self._token_target = token_target
        self._token_data_type = token_data_type
        self._token_rank = token_rank
        self._vocabulary_size = vocabulary_size
        self._end_tokens = end_tokens
        self._sequence_capacity = sequence_capacity
        self._resources = tuple(resources)
        self._history: list[tuple[str, str]] = []
        self._closed = False

    @property
    def resolved_target(self) -> str | None:
        return _resolved_target_name(self._stage)

    @property
    def history(self) -> tuple[tuple[str, str], ...]:
        return tuple(self._history)

    def ort_diagnostics(self) -> tuple[str | None, str | None, bool] | None:
        """Return public ORT stage diagnostics when the selected backend provides them."""

        return _ort_diagnostics(self._runtime_api, self._stage)

    def reset(self) -> None:
        # Clear Runtime sequence state, tokenizer decoder state, and conversation
        # history to start a new conversation.
        self._pipeline.reset()
        self._decoder.reset()
        self._history.clear()

    # Pack token IDs into a target-bound tensor, bind input ordinal 0, and run
    # the retained decoder stage.
    def _run_tokens(self, token_ids: Sequence[int]) -> None:
        values = tuple(token_ids)
        shape = (len(values),) if self._token_rank == 1 else (1, len(values))
        format_code = (
            "i"
            if self._token_data_type == self._runtime_api.TensorDataType.INT32
            else "q"
        )
        payload = struct.pack(f"<{len(values)}{format_code}", *values)
        with self._runtime.create_tensor(
            self._token_data_type,
            shape,
            payload,
            target=self._token_target,
        ) as token_tensor:
            self._stage.bind_input(0, token_tensor)
            self._pipeline.run()

    # Read only the final-position logits row when the output has a sequence
    # axis, matching the manual decode-loop pattern.
    def _read_last_logits(self) -> Any:
        with self._stage.output(0) as output_tensor:
            _data_type, shape = output_tensor.desc()
            if not shape or shape[-1] <= 0:
                raise ValueError(
                    "language-model logits must have a vocabulary dimension"
                )
            if len(shape) == 1:
                return output_tensor.to_numpy()

            starts = [0] * len(shape)
            extents = [1] * len(shape)
            starts[-2] = shape[-2] - 1
            extents[-1] = shape[-1]
            with output_tensor.region(
                starts,
                extents,
                target=self._token_target,
            ) as final_position:
                return final_position.to_numpy()

    def generate(
        self,
        prompt: str,
        *,
        max_new_tokens: int,
        raw: bool,
        on_fragment: Callable[[str], None] | None = None,
    ) -> Generation:
        """Generate a response, retaining model-template conversation history."""

        if max_new_tokens <= 0:
            raise ValueError("max_new_tokens must be positive")

        self._pipeline.reset()
        self._decoder.reset()
        prompt_tokens, history_added = _encode_prompt(
            self._runtime_api,
            self._tokenizer,
            self._history,
            prompt,
            raw,
        )

        try:
            if not prompt_tokens:
                raise ValueError("the tokenizer produced an empty prompt")
            _validate_token_ids(
                prompt_tokens,
                self._vocabulary_size,
                "prompt",
            )
            capacity = self._sequence_capacity
            if capacity and len(prompt_tokens) >= capacity:
                raise ValueError(
                    "the prompt does not fit the model sequence capacity"
                )

            # Pattern 9: run the prompt, then repeatedly read logits, choose a
            # token, decode a fragment, and feed the token back.
            started = perf_counter()
            self._run_tokens(prompt_tokens)
            fragments: list[str] = []
            token_count = 0
            first_token_at: float | None = None
            stop_reason = "MAX_TOKENS"

            for token_index in range(max_new_tokens):
                if capacity and self._stage.sequence_position >= capacity:
                    stop_reason = "CAPACITY"
                    break
                logits = self._read_last_logits()
                token_id = _last_token_argmax(logits)
                if token_id in self._end_tokens:
                    stop_reason = "EOS"
                    break
                if first_token_at is None:
                    first_token_at = perf_counter()
                fragment = self._decoder.decode_token(token_id)
                fragments.append(fragment)
                token_count += 1
                if on_fragment is not None and fragment:
                    on_fragment(fragment)
                if capacity and self._stage.sequence_position + 1 >= capacity:
                    stop_reason = "CAPACITY"
                    break
                if token_index + 1 < max_new_tokens:
                    self._run_tokens((token_id,))

            completed = perf_counter()
            generation = Generation(
                text="".join(fragments),
                token_count=token_count,
                time_to_first_token_seconds=(
                    first_token_at - started
                    if first_token_at is not None
                    else None
                ),
                total_seconds=completed - started,
                stop_reason=stop_reason,
            )
            if history_added:
                _record_generation(
                    self._runtime_api,
                    self._history,
                    generation,
                )
                history_added = False
            return generation
        finally:
            if history_added:
                self._history.pop()

    def close(self) -> None:
        if not self._closed:
            self._closed = True
            _close_all(self._resources)

    def __enter__(self) -> "UnifiedLanguageRunner":
        return self

    def __exit__(self, *_args: object) -> None:
        self.close()


def create_unified_runner(
    model_path: str | Path,
    request: LanguageRequest,
) -> UnifiedLanguageRunner:
    """Create a primitive runner for an ONNX/ORT or GGUF artifact."""

    path = Path(model_path)
    artifact = language_artifact_kind(path)
    _validate_request(request, artifact)

    runtime_api = _runtime_api()
    runtime = runtime_api.Runtime()
    stage_target = hardware_target = None
    try:
        stage_target, hardware_target = _create_target(runtime_api, runtime, request)
        return _create_primitive_runner(
            runtime_api,
            runtime,
            stage_target,
            hardware_target,
            path,
            artifact,
            request,
        )
    except Exception as error:
        _close_all(
            (stage_target, runtime)
            if hardware_target is stage_target
            else (stage_target, hardware_target, runtime),
            primary_error=error,
        )
        raise


# Builds the unified Runtime pipeline and tokenizer objects retained by
# UnifiedLanguageRunner.
def _create_primitive_runner(
    runtime_api: Any,
    runtime: Any,
    stage_target: Any,
    hardware_target: Any,
    model_path: Path,
    artifact: ArtifactKind,
    request: LanguageRequest,
) -> UnifiedLanguageRunner:
    """Build a unified language model entirely from retained Runtime primitives."""

    model = builder = stage = pipeline = tokenizer = decoder = token_target = None
    try:
        # Runtime.load_model selects the backend from the artifact; the rest of
        # this setup is shared for ONNX/ORT and GGUF.
        model = runtime.load_model(str(model_path))
        builder = runtime.create_pipeline_builder()
        stage = builder.add_model_stage(model, stage_target, "decoder")
        # The Runtime publishes only outputs that are requested before the
        # pipeline is built.
        stage.request_output(0)
        # State pairs and capacity hints must be configured before builder.build.
        _, declared_capacity = _declare_trailing_state_pairs(
            runtime_api, model, stage
        )
        capacity_hint = request.context_capacity or declared_capacity
        if capacity_hint:
            stage.set_sequence_capacity_hint(capacity_hint)
        pipeline = builder.build()
        builder.close()
        builder = None
        with stage.schema() as schema:
            token_data_type, token_shape = schema.input_desc(0)
        if token_data_type not in {
            runtime_api.TensorDataType.INT32,
            runtime_api.TensorDataType.INT64,
        }:
            raise ValueError(
                "unified language-model token input must use INT32 or INT64"
            )
        if len(token_shape) not in {1, 2}:
            raise ValueError(
                "unified language-model token input must use rank 1 or 2"
            )
        sequence_capacity = _probe_sequence_capacity(
            runtime_api,
            stage,
            "unified language model",
        )
        tokenizer_source = request.tokenizer_source
        if tokenizer_source is None:
            tokenizer_source = (
                model_path.parent if artifact is ArtifactKind.ONNX else model_path
            )
        tokenizer = runtime_api.Tokenizer.from_file(str(tokenizer_source))
        vocabulary_size, end_tokens = _discover_vocabulary(stage, tokenizer)
        decoder = tokenizer.create_decoder()
        token_target = runtime.create_cpu_target()
        resources = [
            decoder,
            tokenizer,
            pipeline,
            stage,
            model,
            token_target,
            stage_target,
        ]
        if hardware_target is not stage_target:
            resources.append(hardware_target)
        resources.append(runtime)
        return UnifiedLanguageRunner(
            runtime_api,
            runtime,
            pipeline,
            stage,
            tokenizer,
            decoder,
            token_target,
            token_data_type,
            len(token_shape),
            vocabulary_size,
            end_tokens,
            sequence_capacity,
            resources,
        )
    except Exception as error:
        _close_all(
            (
                decoder,
                tokenizer,
                pipeline,
                stage,
                builder,
                model,
                token_target,
            ),
            primary_error=error,
        )
        raise


def _stage_input_desc(stage: Any, index: int) -> tuple[Any, Any]:
    with stage.schema() as schema:
        return schema.input_desc(index)


def _validate_token_ids(
    token_ids: Iterable[int],
    vocabulary_size: int | None,
    label: str,
) -> None:
    """Reject token ids the model's vocabulary cannot represent.

    ``vocabulary_size`` is None when the backend left the logits dimension
    free; range validation is then skipped rather than guessed at.
    """

    for token_id in token_ids:
        if (
            isinstance(token_id, bool)
            or not isinstance(token_id, int)
            or token_id < 0
            or (vocabulary_size is not None and token_id >= vocabulary_size)
        ):
            raise ValueError(
                f"{label} token {token_id!r} is outside the model vocabulary "
                f"of {vocabulary_size if vocabulary_size is not None else 'unknown'} "
                "entries"
            )


def _encode_prompt(
    runtime_api: Any,
    tokenizer: Any,
    history: list[tuple[str, str]],
    prompt: str,
    raw: bool,
) -> tuple[Sequence[int], bool]:
    """Encode one user turn and report whether it was added to history."""

    if raw:
        return tokenizer.encode(
            prompt,
            add_special_tokens=True,
        ), False

    # The structured conversation formatter renders the model's own chat
    # template over the whole history and appends the generation prompt. The
    # user turn joins the history only after it formats successfully.
    turns = (*history, (runtime_api.ChatRole.USER, prompt))
    request = runtime_api.ChatCompletionRequest(
        messages=tuple(
            runtime_api.ChatCompletionMessage(role=role, content=content)
            for role, content in turns
        ),
    )
    with tokenizer.format_conversation(request) as formatted:
        token_ids = list(formatted.token_ids)
    history.append((runtime_api.ChatRole.USER, prompt))
    return token_ids, True


def _record_generation(
    runtime_api: Any,
    history: list[tuple[str, str]],
    generation: Generation,
) -> None:
    history.append((runtime_api.ChatRole.ASSISTANT, generation.text))


def _discover_vocabulary(
    stage: Any,
    tokenizer: Any,
) -> tuple[int | None, frozenset[int]]:
    """Cache the logits vocabulary and validate tokenizer special tokens."""

    with stage.schema() as schema:
        _data_type, output_shape = schema.output_desc(0)
    vocabulary_size = _fixed_size(output_shape[-1]) if output_shape else None

    eos_tokens = tokenizer.eos_token_ids
    if not eos_tokens:
        raise ValueError("tokenizer metadata must expose at least one EOS token")
    special_tokens = list(eos_tokens)
    beginning_token = tokenizer.bos_token_id
    if beginning_token is not None:
        special_tokens.append(beginning_token)
    _validate_token_ids(special_tokens, vocabulary_size, "tokenizer special")
    return vocabulary_size, frozenset(eos_tokens)


def _sample_artifact(directory: Path, name: str) -> Path:
    path = directory / name
    if not path.is_file():
        raise FileNotFoundError(f"Split sample artifact not found: {path}")
    return path


def _element_count(shape: Sequence[int], label: str) -> int:
    if not shape or any(dimension <= 0 for dimension in shape):
        raise ValueError(f"{label} must have a fixed positive shape")
    count = 1
    for dimension in shape:
        count *= dimension
    return count


def _create_tensor(
    runtime_api: Any,
    runtime: Any,
    data_type: Any,
    shape: Sequence[int],
    values: Sequence[int | float],
    target: Any,
) -> Any:
    data_types = {
        runtime_api.TensorDataType.INT32: "i",
        runtime_api.TensorDataType.INT64: "q",
        runtime_api.TensorDataType.FLOAT16: "e",
        runtime_api.TensorDataType.FLOAT32: "f",
    }
    try:
        element_format = data_types[data_type]
    except KeyError as error:
        raise ValueError(f"unsupported split tensor data type: {data_type!r}") from error
    element_count = _element_count(shape, "split tensor")
    if len(values) != element_count:
        raise ValueError("split tensor value count does not match its shape")
    payload = struct.pack(f"<{element_count}{element_format}", *values)
    return runtime.create_tensor(data_type, shape, payload, target=target)


# Owns the explicit split ONNX pipeline and caller-driven greedy decode loop.
class SplitLanguageRunner:
    """Owns the sample's explicit split pipeline and greedy decode loop."""

    def __init__(
        self,
        runtime_api: Any,
        runtime: Any,
        pipeline: Any,
        stages: dict[str, Any],
        decoder_target: Any,
        token_target: Any,
        tokenizer: Any,
        decoder: Any,
        token_data_type: Any,
        token_shape: Sequence[int],
        position_data_type: Any,
        position_shape: Sequence[int],
        mask_data_type: Any,
        mask_shape: Sequence[int],
        sequence_capacity: int,
        vocabulary_size: int | None,
        end_tokens: frozenset[int],
        resources: Sequence[Any],
    ) -> None:
        self._runtime_api = runtime_api
        self._runtime = runtime
        self._pipeline = pipeline
        self._stages = stages
        self._decoder_target = decoder_target
        self._token_target = token_target
        self._tokenizer = tokenizer
        self._decoder = decoder
        self._token_data_type = token_data_type
        self._token_shape = tuple(token_shape)
        self._position_data_type = position_data_type
        self._position_shape = tuple(position_shape)
        self._mask_data_type = mask_data_type
        self._mask_shape = tuple(mask_shape)
        self._sequence_capacity = sequence_capacity
        self._vocabulary_size = vocabulary_size
        self._end_tokens = end_tokens
        self._resources = tuple(resources)
        self._history: list[tuple[str, str]] = []
        self._closed = False

    @property
    def resolved_target(self) -> str | None:
        return _resolved_target_name(self._stages["decoder"])

    @property
    def history(self) -> tuple[tuple[str, str], ...]:
        return tuple(self._history)

    def ort_diagnostics(self) -> tuple[str | None, str | None, bool] | None:
        return _ort_diagnostics(self._runtime_api, self._stages["decoder"])

    def reset(self) -> None:
        self._reset_execution()
        self._history.clear()

    def _reset_execution(self) -> None:
        self._pipeline.reset()
        self._decoder.reset()

    def _create_tensor(
        self,
        data_type: Any,
        shape: Sequence[int],
        values: Sequence[int | float],
        target: Any,
    ) -> Any:
        return _create_tensor(
            self._runtime_api,
            self._runtime,
            data_type,
            shape,
            values,
            target,
        )

    # One split decode step: bind token, position, and mask tensors, then run
    # embedding -> decoder -> head with Runtime-managed decoder state.
    def _run_token(self, token_id: int) -> None:
        state_stage = self._stages["decoder"]
        position = state_stage.sequence_position
        capacity = self._sequence_capacity
        if position >= capacity:
            raise ValueError("split autoregressive sequence capacity is exhausted")

        tensors: list[Any] = []
        try:
            token_tensor = self._create_tensor(
                self._token_data_type,
                self._token_shape,
                [token_id],
                self._token_target,
            )
            tensors.append(token_tensor)
            self._stages["embedding"].bind_input(0, token_tensor)

            position_tensor = self._create_tensor(
                self._position_data_type,
                self._position_shape,
                [position],
                self._decoder_target,
            )
            tensors.append(position_tensor)
            self._stages["decoder"].bind_input(1, position_tensor)

            masked = (
                -65504.0
                if self._mask_data_type == self._runtime_api.TensorDataType.FLOAT16
                else -3.4028234663852886e38
            )
            mask_values = [masked] * capacity
            mask_values[: position + 1] = [0.0] * (position + 1)
            mask_tensor = self._create_tensor(
                self._mask_data_type,
                self._mask_shape,
                mask_values,
                self._decoder_target,
            )
            tensors.append(mask_tensor)
            self._stages["decoder"].bind_input(2, mask_tensor)

            self._pipeline.run()
        finally:
            _close_all(reversed(tensors))

    def generate(
        self,
        prompt: str,
        *,
        max_new_tokens: int,
        raw: bool,
        on_fragment: Callable[[str], None] | None = None,
    ) -> Generation:
        if max_new_tokens <= 0:
            raise ValueError("max_new_tokens must be positive")

        self._reset_execution()
        prompt_tokens, history_added = _encode_prompt(
            self._runtime_api,
            self._tokenizer,
            self._history,
            prompt,
            raw,
        )

        try:
            if not prompt_tokens:
                raise ValueError("the tokenizer produced an empty prompt")
            _validate_token_ids(
                prompt_tokens,
                self._vocabulary_size,
                "prompt",
            )
            capacity = self._sequence_capacity
            if len(prompt_tokens) >= capacity:
                raise ValueError(
                    "the prompt does not fit the split model sequence capacity"
                )

            started = perf_counter()
            for token_id in prompt_tokens:
                self._run_token(token_id)

            output_stage = self._stages["head"]
            end_tokens = self._end_tokens
            fragments: list[str] = []
            token_count = 0
            first_token_at: float | None = None
            stop_reason = "MAX_TOKENS"

            for token_index in range(max_new_tokens):
                state_stage = self._stages["decoder"]
                if state_stage.sequence_position >= capacity:
                    stop_reason = "CAPACITY"
                    break
                with output_stage.output(0) as output_tensor:
                    logits = output_tensor.to_numpy()
                token_id = _last_token_argmax(logits)
                if token_id in end_tokens:
                    stop_reason = "EOS"
                    break
                if first_token_at is None:
                    first_token_at = perf_counter()
                fragment = self._decoder.decode_token(token_id)
                fragments.append(fragment)
                token_count += 1
                if on_fragment is not None and fragment:
                    on_fragment(fragment)
                if state_stage.sequence_position + 1 >= capacity:
                    stop_reason = "CAPACITY"
                    break
                if token_index + 1 < max_new_tokens:
                    self._run_token(token_id)

            completed = perf_counter()
            generation = Generation(
                text="".join(fragments),
                token_count=token_count,
                time_to_first_token_seconds=(
                    first_token_at - started
                    if first_token_at is not None
                    else None
                ),
                total_seconds=completed - started,
                stop_reason=stop_reason,
            )
            if history_added:
                _record_generation(
                    self._runtime_api,
                    self._history,
                    generation,
                )
                history_added = False
            return generation
        finally:
            if history_added:
                self._history.pop()

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        _close_all(self._resources)

    def __enter__(self) -> "SplitLanguageRunner":
        return self

    def __exit__(self, *_args: object) -> None:
        self.close()


def _validate_split_target_request(request: LanguageRequest) -> None:
    if request.ep and request.target_policy:
        raise ValueError(
            "--performance and --efficiency select generic Runtime targets and "
            "cannot be combined with --ep."
        )


# Builds emb.onnx -> decoder.onnx -> head.onnx, declares decoder KV state, and
# returns a runner that drives generation token by token.
def create_split_pipeline(
    model_directory: str | Path,
    request: LanguageRequest,
) -> SplitLanguageRunner:
    """Create a split ORT language model."""

    if request.backend is not Backend.ORT:
        raise ValueError("The split pipeline supports ORT, not GGUF.")
    if request.context_capacity:
        raise ValueError("The split pipeline has an export-defined fixed context capacity.")
    _validate_split_target_request(request)
    directory = Path(model_directory)
    stage_files = (
        ("embedding", _sample_artifact(directory, "emb.onnx")),
        ("decoder", _sample_artifact(directory, "decoder.onnx")),
        ("head", _sample_artifact(directory, "head.onnx")),
    )
    tokenizer_path = _sample_artifact(directory, "tokenizer.json")
    runtime_api = _runtime_api()
    runtime = runtime_api.Runtime()
    decoder_target = hardware_target = builder = pipeline = tokenizer = decoder = None
    resources: list[Any] = [runtime]
    stages: dict[str, Any] = {}
    cpu_target = None
    try:
        decoder_target, hardware_target = _create_target(
            runtime_api,
            runtime,
            request,
        )
        resources.insert(0, decoder_target)
        if hardware_target is not decoder_target:
            resources.insert(0, hardware_target)
        cpu_target = runtime.create_cpu_target()
        resources.insert(0, cpu_target)

        builder = runtime.create_pipeline_builder()
        for stage_id, stage_path in stage_files:
            stage_target = (
                decoder_target if stage_id == "decoder" else cpu_target
            )
            source = runtime.load_model(str(stage_path))
            resources.insert(0, source)
            stage = builder.add_model_stage(source, stage_target, stage_id)
            stages[stage_id] = stage
            resources.insert(0, stage)
            if stage_id == "decoder":
                # Inputs are hidden state, position, and mask, then each past
                # key/value; outputs are hidden state, then each present
                # key/value in the same order.
                with source.schema() as schema:
                    state_count = schema.input_count - 3
                    if state_count < 0 or state_count != schema.output_count - 1:
                        raise ValueError(
                            "split decoder must pair each past input with a "
                            "present output"
                        )
                # Declare each past/present KV pair before Build so the Runtime
                # carries decoder state across pipeline runs.
                for state_index in range(state_count):
                    stage.add_state_tensor_pair(3 + state_index, 1 + state_index)

        # Pattern 4: connect stages by output/input ordinal, then request the
        # final logits output before building.
        builder.connect(stages["embedding"], 0, stages["decoder"], 0)
        builder.connect(stages["decoder"], 0, stages["head"], 0)
        stages["head"].request_output(0)
        pipeline = builder.build()
        resources.insert(0, pipeline)

        schema_sources = stages
        token_data_type, token_shape = _stage_input_desc(
            schema_sources["embedding"],
            0,
        )
        position_data_type, position_shape = _stage_input_desc(
            schema_sources["decoder"],
            1,
        )
        mask_data_type, mask_shape = _stage_input_desc(
            schema_sources["decoder"],
            2,
        )
        if token_data_type not in {
            runtime_api.TensorDataType.INT32,
            runtime_api.TensorDataType.INT64,
        } or _element_count(token_shape, "split token input") != 1:
            raise ValueError(
                "split embedding input 0 must be one INT32 or INT64 token"
            )
        if position_data_type not in {
            runtime_api.TensorDataType.INT32,
            runtime_api.TensorDataType.INT64,
        } or _element_count(position_shape, "split position input") != 1:
            raise ValueError(
                "split decoder input 1 must be one INT32 or INT64 position"
            )
        if mask_data_type not in {
            runtime_api.TensorDataType.FLOAT16,
            runtime_api.TensorDataType.FLOAT32,
        }:
            raise ValueError(
                "split decoder input 2 must be a FLOAT16 or FLOAT32 mask"
            )
        sequence_capacity = mask_shape[-1] if mask_shape else 0
        if (
            sequence_capacity <= 0
            or _element_count(mask_shape, "split attention mask")
            != sequence_capacity
        ):
            raise ValueError(
                "split decoder input 2 must have singleton leading dimensions "
                "and a fixed final sequence dimension"
            )

        state_owner = stages["decoder"]
        try:
            state_owner.set_sequence_capacity(sequence_capacity)
        except runtime_api.NotSupportedError:
            pass
        fixed_capacity = _probe_sequence_capacity(
            runtime_api,
            state_owner,
            "split decoder stage",
        )
        if fixed_capacity != sequence_capacity:
            raise ValueError(
                "The split decoder stage reports a fixed sequence capacity of "
                f"{fixed_capacity}, but its mask schema requires "
                f"{sequence_capacity}."
            )

        builder.close()
        builder = None

        tokenizer = runtime_api.Tokenizer.from_file(str(tokenizer_path))
        resources.insert(0, tokenizer)
        vocabulary_size, end_tokens = _discover_vocabulary(
            schema_sources["head"],
            tokenizer,
        )
        decoder = tokenizer.create_decoder()
        resources.insert(0, decoder)
        return SplitLanguageRunner(
            runtime_api,
            runtime,
            pipeline,
            stages,
            # Decoder inputs are created on the hardware target; a pinned
            # provider target places the stage but does not create tensors.
            hardware_target,
            cpu_target,
            tokenizer,
            decoder,
            token_data_type,
            token_shape,
            position_data_type,
            position_shape,
            mask_data_type,
            mask_shape,
            sequence_capacity,
            vocabulary_size,
            end_tokens,
            resources,
        )
    except Exception as error:
        _close_all(
            (builder, *resources),
            primary_error=error,
        )
        raise

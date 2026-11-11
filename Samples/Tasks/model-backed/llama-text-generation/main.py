# Copyright (C) Microsoft Corporation. All rights reserved.

"""Explicit llama.cpp: load a GGUF file with the llama.cpp loader and stream a reply.

Runtime.load_llama_model is an engine-specific opt-in for text generation;
nothing selects llama.cpp unless the app loads the model this way. A draft
model loaded the same way enables speculative decoding.
"""

from __future__ import annotations

import argparse
from contextlib import ExitStack
from importlib import metadata
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "python"))

from winml_task_samples.entrypoint import run_entry_point  # noqa: E402
from winml_task_samples.model_tasks import (  # noqa: E402
    DEFAULT_CHAT_PROMPT,
    is_not_supported,
    stream_chat_reply,
)
from windowsml.runtime import (  # noqa: E402
    ExecutionTargetKind,
    NotSupportedError,
    Runtime,
    WinMLError,
)
from windowsml.tasks import Tasks, TextGenerationSpeculativeMethod  # noqa: E402

# HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) and HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND)
_MISSING_FILE_HRESULTS = {0x80070002, 0x8007007E}


def _install_payload_guidance() -> str:
    """Name the windowsml-llama-core wheel that matches the installed windowsml."""

    try:
        version = metadata.version("windowsml")
    except metadata.PackageNotFoundError:
        version = "<windowsml version>"
    return (
        "Install the llama.cpp wheel of the same version as windowsml with "
        f"'python -m pip install windowsml-llama-core=={version}'."
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("model", type=Path, help="GGUF model file.")
    parser.add_argument(
        "--draft-model", type=Path, help="Smaller GGUF model with the same tokenizer."
    )
    parser.add_argument("--prompt", default=DEFAULT_CHAT_PROMPT)
    parser.add_argument("--max-new-tokens", type=int, default=64)
    parser.add_argument("--device", choices=("cpu", "gpu"), default="cpu")
    args = parser.parse_args()
    if args.max_new_tokens <= 0:
        parser.error("--max-new-tokens must be positive.")

    with ExitStack() as resources:
        runtime = resources.enter_context(Runtime())
        # The packaged llama.cpp backend runs on the CPU. A GPU target needs a
        # GPU backend that you install yourself.
        target = resources.enter_context(
            runtime.create_target(ExecutionTargetKind.GPU)
            if args.device == "gpu"
            else runtime.create_cpu_target()
        )
        try:
            model = resources.enter_context(runtime.load_llama_model(str(args.model)))
            draft = (
                resources.enter_context(runtime.load_llama_model(str(args.draft_model)))
                if args.draft_model
                else None
            )
        except NotSupportedError as error:
            raise RuntimeError(
                "The llama.cpp model loader is not available. Use a windowsml release "
                "that includes the llama.cpp engine."
            ) from error

        tasks = resources.enter_context(Tasks(runtime))
        configuration = resources.enter_context(tasks.create_text_generation_configuration())
        # set_model reads the tokenizer from the GGUF file. Each session loads the weights.
        configuration.set_model(target, model, draft_model=draft)
        if draft is not None:
            configuration.set_speculative_decoding(TextGenerationSpeculativeMethod.DRAFT_MODEL)

        tokenizer = configuration.tokenizer
        task = resources.enter_context(tasks.create_text_generation_task(tokenizer))
        # Loading and binding succeed without the llama.cpp payload; the first
        # session loads it.
        try:
            session = resources.enter_context(task.create_session(configuration))
        except WinMLError as error:
            if args.device == "gpu" and is_not_supported(error):
                raise RuntimeError(
                    "No installed llama.cpp backend runs on this GPU "
                    "(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)). Use --device cpu, or "
                    "build a llama.cpp GPU backend into this environment."
                ) from error
            if (error.hresult & 0xFFFFFFFF) in _MISSING_FILE_HRESULTS:
                raise RuntimeError(
                    "This sample requires the llama.cpp payload, which is not installed "
                    f"(HRESULT 0x{error.hresult & 0xFFFFFFFF:08X}). "
                    + _install_payload_guidance()
                ) from error
            raise

        result = stream_chat_reply(session, tokenizer, args.prompt, args.max_new_tokens)
        statistics = result.speculative_statistics
        if draft is not None and statistics is not None:
            print(
                f"Speculative decoding: steps={statistics.verification_step_count} "
                f"drafted={statistics.draft_token_count} "
                f"accepted={statistics.accepted_draft_token_count}"
            )
    return 0


if __name__ == "__main__":
    run_entry_point(main)

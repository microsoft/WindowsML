# Copyright (C) Microsoft Corporation. All rights reserved.

"""Bring your own weights: stream a reply from a checkpoint folder or a GGUF file.

Windows ML plans the model for the GPU and creates the model's own tokenizer
from its files. The app builds no pipeline and passes no tokenizer.
"""

from __future__ import annotations

import argparse
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "python"))

from winml_task_samples.entrypoint import run_entry_point  # noqa: E402
from winml_task_samples.model_tasks import (  # noqa: E402
    DEFAULT_CHAT_PROMPT,
    add_program_cache_arguments,
    is_not_supported,
    program_cache_options,
    stream_chat_reply,
)
from windowsml.runtime import ExecutionTargetKind, Runtime, WinMLError  # noqa: E402
from windowsml.tasks import Tasks  # noqa: E402

NOT_SUPPORTED_GUIDANCE = (
    "Windows ML cannot run this model for text generation "
    "(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)). Use a supported checkpoint, such "
    "as Qwen2.5-0.5B-Instruct as a safetensors folder or GGUF file. To run a GGUF "
    "file that Windows ML does not support on llama.cpp instead, load it with "
    "Runtime.load_llama_model; see the llama-text-generation sample."
)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("model", type=Path, help="Checkpoint folder or GGUF file.")
    parser.add_argument("--prompt", default=DEFAULT_CHAT_PROMPT)
    parser.add_argument("--max-new-tokens", type=int, default=64)
    parser.add_argument("--capacity", type=int, help="Largest sequence the session holds.")
    add_program_cache_arguments(parser)
    args = parser.parse_args()
    if args.max_new_tokens <= 0 or (args.capacity is not None and args.capacity <= 0):
        parser.error("Token counts must be positive.")
    cache_options = program_cache_options(parser, args)

    with (
        Runtime() as runtime,
        runtime.create_target(ExecutionTargetKind.GPU) as target,
        runtime.load_model(str(args.model)) as model,
        Tasks(runtime) as tasks,
        tasks.create_text_generation_configuration() as configuration,
    ):
        # No tokenizer: set_model creates the model's own from its files. Sessions
        # compile programs for the GPU; a program cache keeps them for later runs.
        try:
            configuration.set_model(target, model, **cache_options)
        except WinMLError as error:
            if is_not_supported(error):
                raise RuntimeError(NOT_SUPPORTED_GUIDANCE) from error
            raise
        if args.capacity is not None:
            configuration.set_logical_capacity(args.capacity)

        # The task must use the configuration's tokenizer.
        tokenizer = configuration.tokenizer
        with (
            tasks.create_text_generation_task(tokenizer) as task,
            task.create_session(configuration) as session,
        ):
            stream_chat_reply(session, tokenizer, args.prompt, args.max_new_tokens)
    return 0


if __name__ == "__main__":
    run_entry_point(main)

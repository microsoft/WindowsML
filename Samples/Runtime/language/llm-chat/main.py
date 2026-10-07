# Copyright (C) Microsoft Corporation. All rights reserved.

"""LLM chat: compare unified loading with an explicit split pipeline.

Chat with a local language model in the console, or answer one --prompt.
--mode unified loads one decoder; --mode split chains three ONNX models.

  Unified mode uses create_unified_runner, as in hello-language-model.
  Split mode builds from --model-dir: embedding and head on a CPU target,
  the decoder on the selected target with KV pairs declared by
  add_state_tensor_pair, all connected before build. Each token, prompt
  tokens included, is one run binding embedding input 0 (token) and decoder
  inputs 1 and 2 (position, mask). Each reply resets the pipeline and
  re-encodes the history with the chat template (unless --raw). Both modes
  use argmax. --diagnostics prints the decoder's requested and selected
  provider and whether it is pinned. The C++ sample enables verbose logging
  instead.

Run it
  python main.py [--backend ort|llama] [--mode unified|split] [--model <path>]
                 [--model-dir <dir>] [--tokenizer <path>] [--device cpu|gpu|npu]
                 [--ep <name>] [--context <tokens>] [--prompt <text>]
                 [--max-tokens <n>] [--raw] [--no-stream] [--diagnostics]

Learn more (paths relative to this file)
  ../../../../docs/Runtime/python-samples.md
  ../../../../docs/Runtime/tutorials/03-language-models.md
  ../../../../docs/api-reference/CommonPatterns.md
  ../../../../docs/api-reference/IWinMLOrtStageDiagnostics.md
"""

from __future__ import annotations

import argparse
from pathlib import Path
import sys

_RUNTIME_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_RUNTIME_ROOT / "python"))

from winml_runtime_samples.cli import (  # noqa: E402
    add_execution_arguments,
    language_request,
    print_diagnostics,
    reject_bundled_model_on_npu,
    resolve_chat_mode,
    run_chat,
)
from winml_runtime_samples.entrypoint import run_entry_point  # noqa: E402
from winml_runtime_samples.language import (  # noqa: E402
    create_split_pipeline,
    create_unified_runner,
    default_unified_model,
    language_artifact_kind,
)


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run a unified language model or a split ONNX model."
    )
    parser.add_argument(
        "--mode",
        choices=("unified", "split"),
        default=None,
        help=(
            "unified loads one ONNX/ORT or GGUF decoder; split runs the "
            "emb/decoder/head ONNX stages."
        ),
    )
    parser.add_argument("--model", type=Path, help="Unified ONNX/ORT or GGUF decoder.")
    parser.add_argument(
        "--model-dir",
        type=Path,
        help="Split emb/decoder/head ONNX directory for split mode.",
    )
    parser.add_argument("--prompt", help="One-shot prompt. Omit for interactive chat.")
    parser.add_argument(
        "--max-tokens",
        type=int,
        default=256,
        help="Maximum generated tokens for each response.",
    )
    parser.add_argument(
        "--no-stream",
        action="store_true",
        help="Print each completed response after token generation.",
    )
    parser.add_argument(
        "--diagnostics",
        "--verbose",
        dest="diagnostics",
        action="store_true",
        help="Print ONNX Runtime stage diagnostics when available.",
    )
    add_execution_arguments(parser)
    args = parser.parse_args()
    if args.max_tokens <= 0:
        parser.error("--max-tokens must be positive")
    return args


def main() -> int:
    args = _parse_args()
    request = language_request(args)
    reject_bundled_model_on_npu(request, args.model or args.model_dir)
    mode = resolve_chat_mode(request.backend, args.mode)
    split_mode = mode == "split"
    if split_mode:
        if args.model or args.tokenizer:
            raise ValueError("Split mode uses --model-dir, not --model or --tokenizer.")
        if args.context:
            raise ValueError("--context applies only to unified mode.")
        # create_split_pipeline builds emb -> decoder -> head, declares decoder
        # state pairs, and returns a runner for the manual decode loop.
        with create_split_pipeline(
            args.model_dir or _RUNTIME_ROOT / "models" / "llm",
            request,
        ) as runner:
            if runner.resolved_target:
                print(f"Resolved target: {runner.resolved_target}")
            if args.diagnostics:
                print_diagnostics(runner)
            run_chat(
                runner,
                prompt=args.prompt,
                max_tokens=args.max_tokens,
                raw=args.raw,
                stream=not args.no_stream,
            )
        return 0

    if args.model_dir:
        raise ValueError("--model-dir applies only to split mode.")
    model_path = args.model or default_unified_model(_RUNTIME_ROOT, request.backend)
    print(f"Loading {language_artifact_kind(model_path).value.upper()} decoder...")
    # Unified mode loads one artifact; the runner hides Runtime construction but
    # keeps generation caller-driven.
    with create_unified_runner(
        model_path,
        request,
    ) as runner:
        if runner.resolved_target:
            print(f"Resolved target: {runner.resolved_target}")
        if args.diagnostics:
            print_diagnostics(runner)
        run_chat(
            runner,
            prompt=args.prompt,
            max_tokens=args.max_tokens,
            raw=args.raw,
            stream=not args.no_stream,
        )
    return 0


if __name__ == "__main__":
    run_entry_point(main)

# Copyright (C) Microsoft Corporation. All rights reserved.

"""Hello LanguageModel: stream one greedy response from a unified decoder.

Load an ONNX/ORT or GGUF language model, send it one prompt, and print the
reply as it is generated.

  create_unified_runner loads the artifact as one "decoder" stage. For ONNX
  it declares the past/present pairs with add_state_tensor_pair, so the
  Runtime carries the KV cache between runs; a GGUF stage keeps its own.
  generate resets the pipeline, applies the chat template (or plain encoding
  with --raw), and runs the whole prompt once. Each step copies the last
  logits row to a CPU target with region(), takes the argmax, prints the
  fragment, and runs that token, until end of sequence, --max-tokens
  (default 256), or the sequence capacity.

Run it
  python main.py [--backend ort|llama] [--model <path>] [--tokenizer <path>]
                 [--device cpu|gpu|npu] [--ep <name>] [--context <tokens>]
                 [--raw] [--prompt <text>] [--max-tokens <n>]

Learn more (paths relative to this file)
  ../../../../docs/Runtime/python-samples.md
  ../../../../docs/Runtime/tutorials/03-language-models.md
  ../../../../docs/Runtime/tutorials/07-gguf-language-models.md
  ../../../../docs/api-reference/CommonPatterns.md
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
    print_generation,
    reject_bundled_model_on_npu,
)
from winml_runtime_samples.entrypoint import run_entry_point  # noqa: E402
from winml_runtime_samples.language import (  # noqa: E402
    create_unified_runner,
    default_unified_model,
    language_artifact_kind,
)


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run one unified ONNX/ORT or GGUF language-model response."
    )
    parser.add_argument("positional", nargs="*", metavar="VALUE")
    parser.add_argument("--model", type=Path, help="Unified ONNX/ORT or GGUF decoder.")
    parser.add_argument(
        "--prompt",
        default=None,
        help="Prompt text. Defaults to a short greeting request.",
    )
    parser.add_argument(
        "--max-tokens",
        type=int,
        default=256,
        help="Maximum generated tokens.",
    )
    add_execution_arguments(parser)
    args = parser.parse_args()
    if len(args.positional) > 2:
        parser.error("at most model and prompt positional values are allowed")
    if args.max_tokens <= 0:
        parser.error("--max-tokens must be positive")
    return args


def main() -> int:
    args = _parse_args()
    request = language_request(args)
    reject_bundled_model_on_npu(request, args.model)
    positional_model = Path(args.positional[0]) if args.positional else None
    positional_prompt = args.positional[1] if len(args.positional) == 2 else None
    prompt = args.prompt or positional_prompt or "Reply with one short greeting."

    model_path = (
        args.model
        or positional_model
        or default_unified_model(_RUNTIME_ROOT, request.backend)
    )
    print(f"Artifact: {language_artifact_kind(model_path).value.upper()}")
    # create_unified_runner builds and retains the Runtime objects; the runner
    # exposes only the teaching loop used below.
    with create_unified_runner(model_path, request) as runner:
        target = runner.resolved_target
        if target:
            print(f"Resolved target: {target}")
        # generate owns the manual text loop: encode, run, read logits, argmax,
        # feed the selected token back, and stream DecodeToken fragments.
        generation = runner.generate(
            prompt,
            max_new_tokens=args.max_tokens,
            raw=args.raw,
            on_fragment=lambda fragment: print(fragment, end="", flush=True),
        )
        print_generation(generation, stream=True)
    return 0


if __name__ == "__main__":
    run_entry_point(main)

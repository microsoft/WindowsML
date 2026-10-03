# Copyright (C) Microsoft Corporation. All rights reserved.

"""Speech to LanguageModel: ask a language model about supplied transcript text.

Ask an ONNX/ORT or GGUF language model about a transcript and stream its
answer. This sample does not transcribe audio, so --transcript is required.

  The prompt is the instruction, a blank line, "Transcript:", and the
  transcript, sent as one user turn. create_unified_runner loads the model
  as one "decoder" stage, as in hello-language-model, and generate streams
  a greedy reply through the chat template (unless --raw). There is no
  --model flag: a third positional argument replaces the bundled model,
  after two positionals kept only for compatibility with the C++ sample.

Run it
  python main.py --transcript <text> [--backend ort|llama]
                 [--instruction <text>] [--max-tokens <n>] [--device cpu|gpu|npu]
                 [--ep <name>] [--tokenizer <path>] [--context <tokens>] [--raw]

Learn more (paths relative to this file)
  ../../../../docs/Runtime/python-samples.md
  ../../../../docs/Runtime/tutorials/04-speech-to-language.md
  ../../../../docs/Runtime/tutorials/07-gguf-language-models.md
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

_DEFAULT_INSTRUCTION = (
    "What color is the fox in the transcript? Reply with only the color."
)


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Generate a language-model response from a supplied transcript."
    )
    parser.add_argument(
        "positional",
        nargs="*",
        metavar="VALUE",
        help="C++-compatible positions: whisper directory, WAV, language model, instruction.",
    )
    parser.add_argument(
        "--transcript",
        help="Transcript text to send to the language model.",
    )
    parser.add_argument(
        "--instruction",
        help="Language instruction. Cannot be combined with the fourth positional value.",
    )
    parser.add_argument(
        "--max-tokens",
        type=int,
        default=256,
        help="Maximum generated language tokens.",
    )
    add_execution_arguments(parser)
    args = parser.parse_args()
    if len(args.positional) > 4:
        parser.error("at most four C++-compatible positional values are allowed")
    if args.instruction and len(args.positional) == 4:
        parser.error("--instruction cannot be combined with the fourth positional value")
    if args.max_tokens <= 0:
        parser.error("--max-tokens must be positive")
    return args


def main() -> int:
    args = _parse_args()
    request = language_request(args)
    reject_bundled_model_on_npu(request, len(args.positional) >= 3)
    instruction = args.instruction or (
        args.positional[3] if len(args.positional) == 4 else _DEFAULT_INSTRUCTION
    )
    language_model = (
        Path(args.positional[2])
        if len(args.positional) >= 3
        else default_unified_model(_RUNTIME_ROOT, request.backend)
    )

    if args.transcript is None:
        raise ValueError(
            "This tutorial composes two independent task results, so it takes the "
            "transcript from a caller-owned speech recognizer. Run the Whisper "
            "sample (speech\\whisper\\main.py) and pass its text with "
            "--transcript \"...\"."
        )
    print(f"Language backend: {language_artifact_kind(language_model).value.upper()}")
    prompt = f"{instruction}\n\nTranscript:\n{args.transcript}"
    # The transcript is untrusted prompt content; the runner owns only the
    # language-model Runtime pipeline and tokenizer loop.
    with create_unified_runner(language_model, request) as runner:
        print("Response:")
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

# Copyright (C) Microsoft Corporation. All rights reserved.

"""Compose independent ASR and text-generation Tasks with a prompt string.

Transcribe a WAV file with the ASR Task, then ask an ONNX/ORT or GGUF model
about the transcript through the Text Generation Task and stream the reply.

  transcribe drains the ASR stream and returns the transcript, which is
  printed. generate_text then sends the instruction, a blank line,
  "Transcript:", and the transcript as one user turn with the chat template
  and streams a greedy reply. Each helper creates its own Runtime and Task,
  so unlike the C++ sample no Runtime is shared and no cancellation source
  is passed.

Run it
  python main.py <wav> --speech-model-dir <dir> --language-model <path>
                 [--language-backend auto|ort|llama] [--tokenizer <path>]
                 [--instruction <text>] [--max-new-tokens <count>]

Learn more (paths relative to this file)
  README.md
  ../../../../docs/Tasks/task-lifecycle.md
  ../../../../docs/api-reference/IWinMLTasks.md
  ../../../../docs/api-reference/IWinMLAutomaticSpeechRecognitionTask.md
  ../../../../docs/api-reference/IWinMLTextGenerationTask.md
"""

from __future__ import annotations

import argparse
from pathlib import Path
import sys

_TASKS_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_TASKS_ROOT / "python"))

from winml_task_samples.entrypoint import FragmentPrinter, run_entry_point  # noqa: E402
from winml_task_samples.speech_recognition import transcribe  # noqa: E402
from winml_task_samples.text_generation import generate_text  # noqa: E402


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Pass an ASR transcript into an independent Text Generation Task."
    )
    parser.add_argument("wav", type=Path, help="Uncompressed PCM16 WAV file.")
    parser.add_argument("--speech-model-dir", type=Path, required=True)
    parser.add_argument("--language-model", type=Path, required=True)
    parser.add_argument(
        "--language-backend",
        choices=("auto", "ort", "llama"),
        default="auto",
        help="Backend for the independent Text Generation Task.",
    )
    parser.add_argument(
        "--tokenizer",
        type=Path,
        help="Language tokenizer file or directory. Defaults to the model directory.",
    )
    parser.add_argument(
        "--instruction",
        default=(
            "What color is the fox in the transcript? Reply with only the color."
        ),
        help="Instruction placed before the transcript.",
    )
    parser.add_argument("--max-new-tokens", type=int, default=32)
    args = parser.parse_args()
    if args.max_new_tokens <= 0:
        parser.error("--max-new-tokens must be positive")
    return args


def main() -> int:
    args = _parse_args()
    # Run ASR to completion before creating the language prompt.
    transcription = transcribe(args.speech_model_dir, args.wav)
    print(f"Transcript: {transcription.transcript}")

    # The app owns the boundary between Tasks, so it can inspect or transform the
    # transcript before starting text generation. The request is sent as a chat
    # turn, so an instruct model answers and then ends its turn.
    request = f"{args.instruction}\n\nTranscript:\n{transcription.transcript}"
    tokenizer = args.tokenizer or args.language_model.parent
    # Text generation uses a separate Task stream from the ASR stream.
    response = FragmentPrinter("Generated response: ")
    generation = generate_text(
        args.language_model,
        tokenizer,
        request,
        args.max_new_tokens,
        response,
        args.language_backend,
        chat=True,
    )
    print()
    print(f"Finish reason: {generation.finish_reason.name}")
    return 0


if __name__ == "__main__":
    run_entry_point(main)

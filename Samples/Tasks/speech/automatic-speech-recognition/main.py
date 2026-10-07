# Copyright (C) Microsoft Corporation. All rights reserved.

"""Transcribe a WAV file with a Windows ML Automatic Speech Recognition Task.

Transcribe a WAV file with Whisper and print the transcript as it arrives.

  The sample reads 16-bit PCM, mixes it to mono, and resamples it to 16 kHz
  with NumPy. On one CPU target it builds the encoder and decoder stages,
  then creates, validates, and assigns the Task's Whisper configuration.
  tensor_from_numpy and the waveform metadata feed transcribe_waveform on
  the default session, which yields transcript fragments; the streamed text
  must match the final transcript, and the finish reason is printed.

Run it
  python main.py <wav> --model-dir <dir>

Learn more (paths relative to this file)
  README.md
  ../../../../docs/Tasks/task-lifecycle.md
  ../../../../docs/api-reference/IWinMLTasks.md
  ../../../../docs/api-reference/IWinMLAutomaticSpeechRecognitionTask.md
"""

from __future__ import annotations

import argparse
from pathlib import Path
import sys

_TASKS_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_TASKS_ROOT / "python"))

from winml_task_samples.entrypoint import FragmentPrinter, run_entry_point  # noqa: E402
from winml_task_samples.speech_recognition import transcribe  # noqa: E402


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Compose a Whisper ASR Task over Runtime encoder and decoder pipelines."
    )
    parser.add_argument("wav", type=Path, help="Uncompressed PCM16 WAV file.")
    parser.add_argument(
        "--model-dir",
        type=Path,
        required=True,
        help="Directory containing encoder_model.onnx, decoder_model.onnx, and tokenizer.json.",
    )
    return parser.parse_args()


def main() -> int:
    args = _parse_args()

    # transcribe builds the Runtime encoder and decoder pipelines, creates the
    # ASR Task session, and streams the transcript.
    fragments = FragmentPrinter("Transcript: ")
    result = transcribe(args.model_dir, args.wav, fragments)
    print()
    # The final Task result carries the complete transcript and finish status.
    fragments.require_matches(
        result.transcript,
        "Streamed transcript did not match the completed Task result.",
    )
    print(f"Finish reason: {result.finish_reason.name}")
    return 0


if __name__ == "__main__":
    run_entry_point(main)

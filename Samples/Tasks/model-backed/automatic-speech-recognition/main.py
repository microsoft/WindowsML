# Copyright (C) Microsoft Corporation. All rights reserved.

"""Transcribe a PCM WAV file with published speech model weights.

The app binds a speech model folder to the speech recognition task and passes
the model's tokenizer explicitly.
"""

from __future__ import annotations

import argparse
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "python"))

from winml_task_samples.entrypoint import FragmentPrinter, run_entry_point  # noqa: E402
from winml_task_samples.model_tasks import is_not_supported  # noqa: E402
from winml_task_samples.speech_recognition import _load_waveform  # noqa: E402
from windowsml.runtime import ExecutionTargetKind, Runtime, Tokenizer, WinMLError  # noqa: E402
from windowsml.tasks import (  # noqa: E402
    AutomaticSpeechRecognitionMode,
    AutomaticSpeechRecognitionWaveformMetadata,
    Tasks,
)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("model", type=Path, help="Speech model folder.")
    parser.add_argument("wav", type=Path, help="Uncompressed PCM WAV file.")
    parser.add_argument(
        "--tokenizer", type=Path, help="Defaults to tokenizer.json in the model folder."
    )
    parser.add_argument("--language", help="Language tag; omit for the model's default.")
    parser.add_argument("--mode", choices=("transcribe", "translate"), default="transcribe")
    args = parser.parse_args()
    tokenizer_path = args.tokenizer or args.model / "tokenizer.json"
    # Reuse the existing sample's PCM decoding, mono mixing and 16 kHz resampling.
    samples = _load_waveform(args.wav)

    with (
        Runtime() as runtime,
        runtime.create_target(ExecutionTargetKind.GPU) as target,
        runtime.create_cpu_target() as waveform_target,
        runtime.load_model(str(args.model)) as model,
        Tokenizer.from_file(str(tokenizer_path)) as tokenizer,
        Tasks(runtime) as tasks,
        tasks.create_automatic_speech_recognition_task(tokenizer) as task,
        task.create_configuration() as configuration,
    ):
        try:
            configuration.set_model(target, model)
        except WinMLError as error:
            if is_not_supported(error):
                raise RuntimeError(
                    "Windows ML cannot run this model for speech recognition "
                    "(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)). Use a supported speech "
                    "model folder, such as openai/whisper-tiny."
                ) from error
            raise
        configuration.language = args.language
        configuration.mode = (
            AutomaticSpeechRecognitionMode.TRANSLATE
            if args.mode == "translate"
            else AutomaticSpeechRecognitionMode.TRANSCRIBE
        )
        configuration.validate()
        try:
            session = task.create_session(configuration)
        except WinMLError as error:
            if args.mode == "translate" and is_not_supported(error):
                raise RuntimeError(
                    "Models-backed speech recognition transcribes only; this Windows ML "
                    "release doesn't translate with a model-backed configuration "
                    "(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED))."
                ) from error
            raise
        # The waveform stays in host memory; the task moves it to the GPU.
        with (
            session,
            runtime.tensor_from_numpy(samples, target=waveform_target) as waveform,
            session.transcribe_waveform(
                waveform,
                AutomaticSpeechRecognitionWaveformMetadata(
                    sample_rate=16_000,
                    valid_sample_count=samples.shape[1],
                ),
            ) as stream,
        ):
            fragments = FragmentPrinter("Transcript: ")
            for update in stream:
                fragments(update.fragment)
            result = stream.result
            result.raise_for_error()
            print()
            fragments.require_matches(result.transcript, "Stream and final transcript differ.")
            print(f"Finished because: {result.finish_reason.name}")
    return 0


if __name__ == "__main__":
    run_entry_point(main)

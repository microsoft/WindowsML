# Copyright (C) Microsoft Corporation. All rights reserved.

"""Build and run Whisper ASR Tasks for the Python samples."""

from __future__ import annotations

from collections.abc import Callable
from contextlib import ExitStack
from pathlib import Path
import wave

import numpy as np
from windowsml.runtime import Runtime, Tokenizer
from windowsml.tasks import (
    AutomaticSpeechRecognitionResult,
    AutomaticSpeechRecognitionWaveformMetadata,
    Tasks,
)

_WHISPER_SAMPLE_RATE = 16_000


def _load_waveform(path: Path) -> np.ndarray:
    """Read PCM16 WAV input and produce normalized mono 16 kHz samples."""

    if not path.is_file():
        raise FileNotFoundError(f"WAV file was not found: {path}")
    with wave.open(str(path), "rb") as wav_file:
        if wav_file.getcomptype() != "NONE" or wav_file.getsampwidth() != 2:
            raise ValueError("The speech sample requires an uncompressed PCM16 WAV file.")
        channels = wav_file.getnchannels()
        sample_rate = wav_file.getframerate()
        samples = np.frombuffer(
            wav_file.readframes(wav_file.getnframes()),
            dtype="<i2",
        ).astype(np.float32)

    samples = samples.reshape(-1, channels).mean(axis=1) / 32768.0
    if sample_rate != _WHISPER_SAMPLE_RATE:
        output_count = round(samples.size * _WHISPER_SAMPLE_RATE / sample_rate)
        source_positions = (
            np.arange(output_count, dtype=np.float64)
            * sample_rate
            / _WHISPER_SAMPLE_RATE
        )
        samples = np.interp(
            source_positions,
            np.arange(samples.size, dtype=np.float64),
            samples,
        ).astype(np.float32)
    return samples.reshape(1, -1)


def transcribe(
    model_directory: Path,
    wav_path: Path,
    on_fragment: Callable[[str], None] | None = None,
) -> AutomaticSpeechRecognitionResult:
    """Compose the typed Whisper Task over caller-owned Runtime pipelines."""

    def resolve_model(stem: str) -> Path:
        candidate = model_directory / f"{stem}.onnx"
        if candidate.is_file():
            return candidate
        raise FileNotFoundError(
            f"Whisper {stem}.onnx was not found in {model_directory}"
        )

    encoder_path = resolve_model("encoder_model")
    decoder_path = resolve_model("decoder_model")
    tokenizer_path = model_directory / "tokenizer.json"
    if not tokenizer_path.is_file():
        raise FileNotFoundError(f"Whisper input was not found: {tokenizer_path}")
    waveform_data = _load_waveform(wav_path)

    with ExitStack() as resources:
        # The Runtime creates the targets, models, pipelines, and tensor supplied
        # to the typed ASR configuration.
        runtime = resources.enter_context(Runtime())
        target = resources.enter_context(runtime.create_cpu_target())

        # The encoder and decoder are separate Runtime pipelines that the ASR
        # Task binds together through its Whisper configuration.
        encoder_model = resources.enter_context(
            runtime.load_model(str(encoder_path))
        )
        encoder_builder = resources.enter_context(runtime.create_pipeline_builder())
        encoder_stage = resources.enter_context(
            encoder_builder.add_model_stage(
                encoder_model,
                target,
                "python-task-whisper-encoder",
            )
        )
        encoder_stage.symbolic_dimensions["feature_size"] = 80
        encoder_stage.symbolic_dimensions["encoder_sequence_length"] = 3000
        # This prepared encoder needs two ONNX Runtime optimizations disabled.
        # Keep that model-specific session configuration on the encoder stage.
        with encoder_stage.ort_options() as options:
            options.set_session_config(
                "optimization.disable_specified_optimizers",
                "NhwcTransformer;ConvActivationFusion",
            )
        encoder_pipeline = resources.enter_context(encoder_builder.build())

        decoder_model = resources.enter_context(
            runtime.load_model(str(decoder_path))
        )
        decoder_builder = resources.enter_context(runtime.create_pipeline_builder())
        decoder_stage = resources.enter_context(
            decoder_builder.add_model_stage(
                decoder_model,
                target,
                "python-task-whisper-decoder",
            )
        )
        decoder_pipeline = resources.enter_context(decoder_builder.build())

        tokenizer = resources.enter_context(Tokenizer.from_file(str(tokenizer_path)))
        # IWinMLTasks creates the typed ASR Task over the same Runtime identity.
        tasks = resources.enter_context(Tasks(runtime))
        task = resources.enter_context(
            tasks.create_automatic_speech_recognition_task(tokenizer)
        )
        configuration = resources.enter_context(task.create_configuration())
        configuration.configure_whisper(
            encoder_pipeline=encoder_pipeline,
            encoder_stage=encoder_stage,
            decoder_pipeline=decoder_pipeline,
            decoder_stage=decoder_stage,
            tensor_target=target,
        )
        configuration.validate()
        task.default_configuration = configuration

        # Transcription starts from a Runtime tensor plus waveform metadata.
        waveform = resources.enter_context(
            runtime.tensor_from_numpy(waveform_data, target=target)
        )
        metadata = AutomaticSpeechRecognitionWaveformMetadata(
            sample_rate=_WHISPER_SAMPLE_RATE,
            valid_sample_count=waveform_data.shape[1],
        )
        # The pull stream yields transcript fragments; the terminal result carries
        # the complete transcript and finish status.
        stream = resources.enter_context(
            task.default_session.transcribe_waveform(waveform, metadata)
        )
        for update in stream:
            if on_fragment is not None:
                on_fragment(update.fragment)
        result = stream.result
        result.raise_for_error()
        return result

# Copyright (C) Microsoft Corporation. All rights reserved.

"""Whisper WAV, log-mel, and vocabulary helpers for the Runtime samples.

The Runtime-facing audio tensorization lives in the entry point; this module
keeps pure WAV parsing, feature math, and token decoding out of that flow.
"""

from __future__ import annotations

import codecs
from dataclasses import dataclass
import json
from pathlib import Path
import struct
from typing import Any

from .media import import_numpy


WHISPER_SAMPLE_RATE = 16_000
WHISPER_MEL_BINS = 80
WHISPER_TIME_FRAMES = 3_000
WHISPER_MAX_SAMPLES = WHISPER_SAMPLE_RATE * 30
WHISPER_FFT_SIZE = 400
WHISPER_HOP_LENGTH = 160
WHISPER_VOCAB_SIZE = 51_865
WHISPER_MAX_DECODE_TOKENS = 128
WHISPER_MAX_SOURCE_CHANNELS = 8
WHISPER_MAX_PCM_BYTES = 64 * 1024 * 1024

SOT_TOKEN = 50_258
EOT_TOKEN = 50_257
ENGLISH_TOKEN = 50_259
TRANSCRIBE_TOKEN = 50_359
NO_TIMESTAMPS_TOKEN = 50_363


@dataclass(frozen=True)
class WavPcm:
    """PCM payload and format metadata from one RIFF/WAVE file."""

    data: bytes
    sample_rate: int
    channels: int
    sample_format: str
    frame_count: int

    @property
    def duration_seconds(self) -> float:
        return self.frame_count / self.sample_rate


def load_wav_pcm(path: Path) -> WavPcm:
    """Read 16-bit PCM or 32-bit IEEE-float WAV data without another package."""

    format_chunk: bytes | None = None
    data_offset: int | None = None
    data_size = 0
    file_size = path.stat().st_size
    with path.open("rb") as wav_file:
        header = wav_file.read(12)
        if (
            len(header) != 12
            or header[:4] != b"RIFF"
            or header[8:12] != b"WAVE"
        ):
            raise ValueError(f"Not a RIFF/WAVE file: {path}")

        while wav_file.tell() + 8 <= file_size:
            chunk_header = wav_file.read(8)
            if len(chunk_header) != 8:
                break
            chunk_id, chunk_size = struct.unpack("<4sI", chunk_header)
            chunk_offset = wav_file.tell()
            chunk_end = chunk_offset + chunk_size
            if chunk_end > file_size:
                raise ValueError(f"WAV chunk extends beyond the file: {path}")
            if chunk_id == b"fmt ":
                if chunk_size < 16:
                    raise ValueError(f"WAV fmt chunk is too small: {path}")
                format_chunk = wav_file.read(16)
                if len(format_chunk) != 16:
                    raise ValueError(f"WAV fmt chunk is truncated: {path}")
                wav_file.seek(chunk_size - 16, 1)
            elif chunk_id == b"data" and data_offset is None:
                data_offset = chunk_offset
                data_size = chunk_size
                wav_file.seek(chunk_size, 1)
            else:
                wav_file.seek(chunk_size, 1)
            if chunk_size & 1:
                wav_file.seek(1, 1)

        if format_chunk is None or len(format_chunk) < 16 or data_offset is None:
            raise ValueError(f"WAV file is missing fmt or data: {path}")

        (
            format_code,
            channels,
            sample_rate,
            _byte_rate,
            block_align,
            bits_per_sample,
        ) = struct.unpack_from("<HHIIHH", format_chunk)
        if format_code == 1 and bits_per_sample == 16:
            sample_format = "int16"
        elif format_code == 3 and bits_per_sample == 32:
            sample_format = "float32"
        else:
            raise ValueError(
                "Whisper accepts 16-bit PCM or 32-bit IEEE-float WAV input."
            )
        if channels == 0 or channels > WHISPER_MAX_SOURCE_CHANNELS:
            raise ValueError(
                f"WAV channel count must be between 1 and "
                f"{WHISPER_MAX_SOURCE_CHANNELS}: {path}"
            )
        expected_block_align = channels * bits_per_sample // 8
        if (
            sample_rate == 0
            or block_align != expected_block_align
            or data_size % block_align
        ):
            raise ValueError(f"WAV format metadata is inconsistent: {path}")

        maximum_source_frames = (
            WHISPER_MAX_SAMPLES * sample_rate + WHISPER_SAMPLE_RATE - 1
        ) // WHISPER_SAMPLE_RATE
        frame_count = min(data_size // block_align, maximum_source_frames)
        pcm_byte_count = frame_count * block_align
        if pcm_byte_count > WHISPER_MAX_PCM_BYTES:
            raise ValueError(
                f"WAV PCM payload exceeds the {WHISPER_MAX_PCM_BYTES}-byte limit: "
                f"{path}"
            )
        wav_file.seek(data_offset)
        pcm_data = wav_file.read(pcm_byte_count)
        if len(pcm_data) != pcm_byte_count:
            raise ValueError(f"WAV data is truncated: {path}")

    return WavPcm(
        data=pcm_data,
        sample_rate=sample_rate,
        channels=channels,
        sample_format=sample_format,
        frame_count=frame_count,
    )


def resample_mono(samples: Any, source_rate: int) -> Any:
    """Linearly resample a mono waveform to Whisper's 16 kHz contract."""

    np = import_numpy()
    waveform = np.asarray(samples, dtype=np.float32).reshape(-1)
    if source_rate == WHISPER_SAMPLE_RATE:
        return waveform
    if source_rate <= 0:
        raise ValueError("source sample rate must be positive")
    if waveform.size == 0:
        return waveform

    ratio = WHISPER_SAMPLE_RATE / source_rate
    output_length = int(waveform.size * ratio)
    source_positions = np.arange(output_length, dtype=np.float64) / ratio
    left = np.floor(source_positions).astype(np.int64)
    right = np.minimum(left + 1, waveform.size - 1)
    fraction = source_positions - left
    return (
        waveform[left] * (1.0 - fraction) + waveform[right] * fraction
    ).astype(np.float32)


def whisper_mel_filterbank() -> Any:
    """Build the 80x201 Slaney-normalized Whisper mel filterbank."""

    np = import_numpy()
    min_log_hz = 1_000.0
    frequency_spacing = 200.0 / 3.0
    min_log_mel = min_log_hz / frequency_spacing
    log_step = np.log(6.4) / 27.0
    max_mel = min_log_mel + np.log(
        (WHISPER_SAMPLE_RATE / 2) / min_log_hz
    ) / log_step

    mel_points = np.linspace(0.0, max_mel, WHISPER_MEL_BINS + 2)
    hz_points = frequency_spacing * mel_points
    logarithmic = mel_points >= min_log_mel
    hz_points[logarithmic] = min_log_hz * np.exp(
        log_step * (mel_points[logarithmic] - min_log_mel)
    )

    fft_frequencies = np.fft.rfftfreq(
        WHISPER_FFT_SIZE,
        1.0 / WHISPER_SAMPLE_RATE,
    )
    frequency_differences = np.diff(hz_points)
    ramps = hz_points[:, None] - fft_frequencies[None, :]
    lower = -ramps[:-2] / frequency_differences[:-1, None]
    upper = ramps[2:] / frequency_differences[1:, None]
    filters = np.maximum(0.0, np.minimum(lower, upper))
    filters *= (2.0 / (hz_points[2:] - hz_points[:-2]))[:, None]
    return filters.astype(np.float32)


def log_mel_spectrogram(samples: Any) -> Any:
    """Compute Whisper's centered STFT, mel projection, and log normalization."""

    np = import_numpy()
    waveform = np.asarray(samples, dtype=np.float32).reshape(-1)
    audio = np.zeros(WHISPER_MAX_SAMPLES, dtype=np.float32)
    copy_length = min(waveform.size, audio.size)
    audio[:copy_length] = waveform[:copy_length]

    padded = np.pad(
        audio,
        (WHISPER_FFT_SIZE // 2, WHISPER_FFT_SIZE // 2),
        mode="reflect",
    )
    frames = np.lib.stride_tricks.sliding_window_view(
        padded,
        WHISPER_FFT_SIZE,
    )[::WHISPER_HOP_LENGTH][:WHISPER_TIME_FRAMES]
    indices = np.arange(WHISPER_FFT_SIZE, dtype=np.float32)
    hann = 0.5 * (
        1.0
        - np.cos(2.0 * np.pi * indices / WHISPER_FFT_SIZE)
    )
    spectrum = np.fft.rfft(frames * hann, axis=1)
    power = np.abs(spectrum).astype(np.float32) ** 2

    mel = whisper_mel_filterbank() @ power.T
    log_mel = np.log10(np.maximum(mel, 1e-10))
    maximum = np.max(log_mel)
    log_mel = np.maximum(log_mel, maximum - 8.0)
    return ((log_mel + 4.0) / 4.0).astype(np.float32)[None, :, :]


def load_vocabulary(path: Path) -> dict[int, str]:
    """Load either id-to-token or token-to-id Whisper vocabulary JSON."""

    payload = json.loads(path.read_text(encoding="utf-8"))
    vocabulary: dict[int, str] = {}
    for key, value in payload.items():
        if isinstance(value, int) and not isinstance(value, bool):
            vocabulary[value] = key
        elif isinstance(value, str):
            vocabulary[int(key)] = value
    return vocabulary


def _gpt2_byte_decoder() -> dict[int, int]:
    direct_bytes = (
        list(range(ord("!"), ord("~") + 1))
        + list(range(0xA1, 0xAD))
        + list(range(0xAE, 0x100))
    )
    code_points = list(direct_bytes)
    code_points.extend(
        range(256, 256 + (256 - len(direct_bytes)))
    )
    encoded_bytes = direct_bytes + [
        value for value in range(256) if value not in direct_bytes
    ]
    return dict(zip(code_points, encoded_bytes))


_GPT2_BYTE_DECODER = _gpt2_byte_decoder()


class WhisperTokenDecoder:
    """Decode Whisper tokens while retaining partial UTF-8 byte sequences."""

    def __init__(self, vocabulary: dict[int, str]) -> None:
        self._vocabulary = vocabulary
        self._decoder = codecs.getincrementaldecoder("utf-8")(errors="replace")

    def decode_token(self, token_id: int) -> str:
        if token_id >= EOT_TOKEN:
            return ""
        token = self._vocabulary.get(token_id)
        if token is None:
            return ""
        try:
            payload = bytes(_GPT2_BYTE_DECODER[ord(character)] for character in token)
        except KeyError as error:
            raise ValueError(
                f"Whisper vocabulary token {token_id} is not byte-level GPT-2 data"
            ) from error
        return self._decoder.decode(payload, final=False)

    def finish(self) -> str:
        return self._decoder.decode(b"", final=True)

# Copyright (C) Microsoft Corporation. All rights reserved.

"""Small image and numeric helpers shared by the Python Runtime samples.

These helpers deliberately stay outside the Runtime API surface: samples use
them for file decode, PNG writing, labels, and simple postprocessing.
"""

from __future__ import annotations

from pathlib import Path
import struct
from typing import Any, Sequence
import zlib


def import_numpy() -> Any:
    """Import NumPy with sample-oriented installation guidance."""

    try:
        import numpy
    except ImportError as error:
        raise RuntimeError(
            "This sample requires NumPy. Install it with "
            "'python.exe -m pip install numpy'."
        ) from error
    return numpy


def import_pillow_image() -> Any:
    """Import Pillow only when an image sample actually runs."""

    try:
        from PIL import Image
    except ImportError as error:
        raise RuntimeError(
            "This sample uses Pillow only to decode image files. "
            "Install it with 'python.exe -m pip install pillow'."
        ) from error
    return Image


def open_image(path: Path) -> Any:
    """Decode an image file and return a detached Pillow image."""

    Image = import_pillow_image()
    with Image.open(path) as source:
        return source.copy()


def save_rgba_png(
    pixels: bytes,
    size: tuple[int, int],
    output_path: Path,
) -> Path:
    """Encode packed RGBA8 bytes as a non-interlaced PNG."""

    width, height = size
    expected_bytes = width * height * 4
    if width <= 0 or height <= 0:
        raise ValueError("PNG dimensions must be positive.")
    if len(pixels) != expected_bytes:
        raise ValueError(
            f"RGBA payload has {len(pixels)} bytes; expected {expected_bytes}."
        )

    def chunk(kind: bytes, payload: bytes) -> bytes:
        checksum = zlib.crc32(kind + payload) & 0xFFFFFFFF
        return (
            struct.pack(">I", len(payload))
            + kind
            + payload
            + struct.pack(">I", checksum)
        )

    stride = width * 4
    scanlines = b"".join(
        b"\x00" + pixels[offset : offset + stride]
        for offset in range(0, len(pixels), stride)
    )
    header = struct.pack(
        ">IIBBBBB",
        width,
        height,
        8,
        6,
        0,
        0,
        0,
    )
    encoded = (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", header)
        + chunk(b"IDAT", zlib.compress(scanlines))
        + chunk(b"IEND", b"")
    )

    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_bytes(encoded)
    return output_path.resolve()


def load_labels(path: Path) -> list[str]:
    """Load one ImageNet label per line."""

    return [
        line.strip()
        for line in path.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]


def softmax(values: Any) -> Any:
    """Convert one logit vector to probabilities without numeric overflow."""

    np = import_numpy()
    flattened = np.asarray(values, dtype=np.float32).reshape(-1)
    shifted = flattened - np.max(flattened)
    exponentials = np.exp(shifted)
    return exponentials / np.sum(exponentials)


def top_indices(values: Sequence[float], count: int) -> list[int]:
    """Return indices ordered from the largest value to the smallest."""

    np = import_numpy()
    flattened = np.asarray(values).reshape(-1)
    return np.argsort(flattened)[-count:][::-1].tolist()

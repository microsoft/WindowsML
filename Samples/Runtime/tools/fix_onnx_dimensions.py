# Copyright (C) Microsoft Corporation. All rights reserved.

"""Fix selected ONNX symbolic dimensions for Whisper preparation.

Used by Samples/Runtime/scripts/prepare_whisper_models.ps1 before the Runtime
loads the prepared encoder and decoder models.
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path

import onnx


def parse_dimension(value: str) -> tuple[str, int]:
    name, separator, raw_value = value.rpartition("=")
    if not separator or not name or not raw_value:
        raise argparse.ArgumentTypeError("dimensions must use NAME=VALUE")
    parsed = int(raw_value)
    if parsed <= 0:
        raise argparse.ArgumentTypeError("dimension values must be positive")
    return name, parsed


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--dim", action="append", required=True, type=parse_dimension)
    args = parser.parse_args()

    replacements = dict(args.dim)
    model = onnx.load(args.input, load_external_data=False)
    replaced: set[str] = set()

    values = list(model.graph.input) + list(model.graph.output) + list(model.graph.value_info)
    for value in values:
        if not value.type.HasField("tensor_type"):
            continue
        for dimension in value.type.tensor_type.shape.dim:
            if dimension.dim_param in replacements:
                name = dimension.dim_param
                dimension.ClearField("dim_param")
                dimension.dim_value = replacements[name]
                replaced.add(name)

    missing = set(replacements) - replaced
    if missing:
        raise RuntimeError(f"symbolic dimensions were not present: {sorted(missing)}")

    onnx.checker.check_model(model)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    temporary = args.output.with_name(
        args.output.stem + ".tmp" + args.output.suffix
    )
    onnx.save(model, temporary)
    os.replace(temporary, args.output)
    print(f"Fixed {len(replaced)} symbolic dimension(s): {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

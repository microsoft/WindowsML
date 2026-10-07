# Copyright (C) Microsoft Corporation. All rights reserved.

"""Remove no-op ONNX Identity nodes for SESR preparation.

Used by Samples/Runtime/scripts/prepare_sesr.ps1 before the Runtime loads the
prepared super-resolution model.
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path

import onnx


def strip_identity_nodes(model: onnx.ModelProto) -> int:
    removed = 0
    while True:
        identity = next(
            (
                node
                for node in model.graph.node
                if node.op_type == "Identity"
                and len(node.input) == 1
                and len(node.output) == 1
            ),
            None,
        )
        if identity is None:
            return removed

        source = identity.input[0]
        output = identity.output[0]
        for node in model.graph.node:
            for index, name in enumerate(node.input):
                if name == output:
                    node.input[index] = source
        for value in model.graph.output:
            if value.name == output:
                value.name = source
        for value in model.graph.value_info:
            if value.name == output:
                value.name = source

        model.graph.node.remove(identity)
        removed += 1


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    model = onnx.load(args.input, load_external_data=False)
    removed = strip_identity_nodes(model)
    onnx.checker.check_model(model)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    temporary = args.output.with_name(
        args.output.stem + ".tmp" + args.output.suffix
    )
    onnx.save(model, temporary)
    os.replace(temporary, args.output)
    print(f"Removed {removed} Identity node(s): {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

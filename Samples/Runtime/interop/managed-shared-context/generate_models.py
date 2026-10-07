# Copyright (C) Microsoft Corporation. All rights reserved.
"""Generate the two ONNX models used by run_managed_shared_context.ps1.

Each model preserves a static [1, 16, 32, 32] tensor shape and adds one through
a Conv + Relu graph, giving the shared-context sample two compatible stages to
connect and validate.
"""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper


def _write_model(
    path: Path,
    weight_scale: float,
    bias_value: float,
    kernel_size: int,
) -> None:
    shape = [1, 16, 32, 32]
    input_info = helper.make_tensor_value_info(
        "input",
        TensorProto.FLOAT,
        shape,
    )
    output_info = helper.make_tensor_value_info(
        "output",
        TensorProto.FLOAT,
        shape,
    )

    # A center-only convolution acts like a per-channel scale while keeping the
    # graph representative of the operators used by the sample.
    weights = np.zeros(
        (16, 16, kernel_size, kernel_size),
        dtype=np.float32,
    )
    center = kernel_size // 2
    for channel in range(16):
        weights[channel, channel, center, center] = weight_scale

    weight_initializer = numpy_helper.from_array(
        weights,
        name="weights",
    )
    bias_initializer = numpy_helper.from_array(
        np.full((16,), bias_value, dtype=np.float32),
        name="bias",
    )
    convolution = helper.make_node(
        "Conv",
        ["input", "weights", "bias"],
        ["convolution"],
        name="convolution",
        pads=[center, center, center, center],
    )
    activation = helper.make_node(
        "Relu",
        ["convolution"],
        ["output"],
        name="activation",
    )
    graph = helper.make_graph(
        [convolution, activation],
        path.stem,
        [input_info],
        [output_info],
        [weight_initializer, bias_initializer],
    )
    model = helper.make_model(
        graph,
        opset_imports=[helper.make_opsetid("", 17)],
    )
    # Newer onnx releases default to an IR version the bundled ONNX Runtime
    # cannot load. Opset 17 needs only IR 8, so pin a version it accepts.
    model.ir_version = 10
    onnx.checker.check_model(model)
    onnx.save(model, path)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("models"),
    )
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)

    _write_model(args.output / "add_one_3x3.onnx", 1.0, 1.0, 3)
    _write_model(args.output / "add_one_1x1.onnx", 1.0, 1.0, 1)


if __name__ == "__main__":
    main()

# Copyright (C) Microsoft Corporation. All rights reserved.

"""Model compilation: compile an ONNX model, reload it, and run it.

Compile a model to files for one target, load the compiled result back, and
run it once.

  The source model loads with runtime.load_model. compile_model_to_file
  gets the target's model_compiler(), writes artifact.onnx and weights.bin
  with compile_to_file, and reloads them with runtime.load_model
  (--output-dir keeps them). Any --device other than cpu needs --ep. After
  build, the sample checks the stage's device (and provider, with --ep).
  Inputs in each declared dtype come from tensor_from_numpy and are bound
  by position; the sample prints how many output values came back without
  checking them. Sinks and app-held buffers are shown only in C++.

Run it
  python main.py [--model <path>] [--output-dir <path>]
                 [--device cpu|gpu|npu] [--ep <name>]
                 [--performance|--efficiency] [--verbose]

Learn more (paths relative to this file)
  README.md
  ../../../../docs/Runtime/python-samples.md
  ../../../../docs/Runtime/tutorials/06-compile-and-deploy.md
  ../../../../docs/api-reference/IWinMLModelCompiler.md
  ../../../../docs/api-reference/CommonPatterns.md (pattern 7)
"""

from __future__ import annotations

import argparse
from contextlib import ExitStack
from dataclasses import replace
from pathlib import Path
import sys
from typing import Any

_RUNTIME_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_RUNTIME_ROOT / "python"))

from winml_runtime_samples.entrypoint import run_entry_point  # noqa: E402
from winml_runtime_samples.inference import (  # noqa: E402
    add_inference_arguments,
    compile_model_to_file,
    enter_execution_targets,
    execution_request,
    import_runtime,
    prepare_output_directory,
    require_files,
    validate_and_print_stage_placement,
    working_artifact_directory,
)
from winml_runtime_samples.media import import_numpy  # noqa: E402


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Compile an ONNX model to files, reload the compiled artifact, "
            "and run the reloaded model."
        )
    )
    parser.add_argument(
        "--model",
        type=Path,
        default=(
            _RUNTIME_ROOT
            / "models"
            / "squeezenet"
            / "SqueezeNet.onnx"
        ),
        help="Source ONNX model.",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        help=(
            "New or empty directory that retains the artifact and sidecars. "
            "Omit it to use a temporary directory that is removed."
        ),
    )
    add_inference_arguments(parser)
    return parser.parse_args()


def _numpy_dtype(runtime_api: Any, data_type: Any) -> Any:
    np = import_numpy()
    mapping = {
        runtime_api.TensorDataType.FLOAT32: np.float32,
        runtime_api.TensorDataType.FLOAT16: np.float16,
        runtime_api.TensorDataType.INT8: np.int8,
        runtime_api.TensorDataType.UINT8: np.uint8,
        runtime_api.TensorDataType.INT16: np.int16,
        runtime_api.TensorDataType.UINT16: np.uint16,
        runtime_api.TensorDataType.INT32: np.int32,
        runtime_api.TensorDataType.UINT32: np.uint32,
        runtime_api.TensorDataType.INT64: np.int64,
        runtime_api.TensorDataType.UINT64: np.uint64,
        runtime_api.TensorDataType.FLOAT64: np.float64,
        runtime_api.TensorDataType.BOOL: np.bool_,
    }
    try:
        return mapping[data_type]
    except KeyError:
        raise ValueError(
            f"The sample cannot synthesize input data for {data_type.name}."
        ) from None


def main() -> int:
    args = _parse_args()
    request = execution_request(args)
    if request.ep is None:
        if request.device != "cpu":
            raise ValueError(
                f"--device {request.device} needs an app-registered execution "
                "provider to compile with. Add --ep <name> for that provider."
            )
        request = replace(request, ep="CPUExecutionProvider")
    if args.model.suffix.casefold() != ".onnx":
        raise ValueError("The compiler requires an ONNX source model.")
    require_files({"source model": args.model})

    runtime_api = import_runtime()
    np = import_numpy()
    print("=== Windows ML Runtime: Model compilation ===\n")

    with ExitStack() as resources:
        if args.output_dir is None:
            output_directory = resources.enter_context(
                working_artifact_directory("model-compilation")
            )
            retained = False
        else:
            output_directory = prepare_output_directory(args.output_dir)
            retained = True

        runtime = resources.enter_context(runtime_api.Runtime())
        # enter_execution_targets (python/winml_runtime_samples/inference.py)
        # creates and retains both the compiler/stage target and tensor target.
        stage_target, tensor_target = enter_execution_targets(
            resources,
            runtime_api,
            runtime,
            request,
        )
        print(f"[1/5] Compiler target created for {request.device.upper()}.")

        # The source model schema supplies ordinal input descriptors used below
        # to synthesize descriptor-compatible validation tensors.
        source_model = resources.enter_context(runtime.load_model(str(args.model)))
        with source_model.schema() as schema:
            input_descriptors = schema.inputs()
        print(
            f"[2/5] Source model loaded with "
            f"{len(input_descriptors)} input(s)."
        )

        artifact_path = output_directory / "artifact.onnx"
        weights_path = Path("weights.bin")
        # compile_model_to_file obtains target.model_compiler(), calls
        # compile_to_file, then reloads the artifact through runtime.load_model.
        compiled_model = resources.enter_context(
            compile_model_to_file(
                runtime,
                stage_target,
                source_model,
                artifact_path,
                external_weights_path=weights_path,
            )
        )
        if not artifact_path.is_file():
            raise RuntimeError(f"Compiler did not create {artifact_path}")
        print(f"[3/5] Compiled artifact written to {artifact_path}.")

        # A compiled artifact is still a model handle: add it to a normal stage,
        # request output ordinal 0, and build the pipeline.
        builder = resources.enter_context(runtime.create_pipeline_builder())
        stage = resources.enter_context(
            builder.add_model_stage(
                compiled_model,
                stage_target,
                "compiled-model",
            )
        )
        # The Runtime publishes only outputs that are requested before the
        # pipeline is built.
        stage.request_output(0)
        pipeline = resources.enter_context(builder.build())
        validate_and_print_stage_placement(stage, request, args.verbose)
        print("[4/5] Compiled artifact reloaded and pipeline built.")

        input_tensors = []
        for index, (data_type, declared_shape) in enumerate(input_descriptors):
            concrete_shape = tuple(dimension or 1 for dimension in declared_shape)
            element_count = int(np.prod(concrete_shape, dtype=np.int64))
            values = (np.arange(element_count) % 7).astype(
                _numpy_dtype(runtime_api, data_type)
            )
            values = values.reshape(concrete_shape)
            # tensor_from_numpy creates a Runtime tensor on the requested target
            # from the descriptor-compatible NumPy array.
            tensor = resources.enter_context(
                runtime.tensor_from_numpy(values, target=tensor_target)
            )
            input_tensors.append(tensor)
            stage.bind_input(index, tensor)

        # Bindings and outputs are positional. stage.output(0) reads the
        # runtime-owned tensor requested before Build.
        pipeline.run()
        with stage.output(0) as output:
            output_values = output.to_numpy()
        print(
            f"[5/5] Reloaded model produced "
            f"{output_values.size} output value(s)."
        )

        generated_files = sorted(
            path.name for path in output_directory.iterdir() if path.is_file()
        )
        print("Generated files:", ", ".join(generated_files))
        if retained:
            print(f"Retained output directory: {output_directory}")
        else:
            print("The temporary output directory will now be removed.")

    return 0


if __name__ == "__main__":
    run_entry_point(main)

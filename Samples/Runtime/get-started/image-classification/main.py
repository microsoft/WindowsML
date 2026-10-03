# Copyright (C) Microsoft Corporation. All rights reserved.

"""Image classification with a one-stage Windows ML Runtime pipeline.

Classify one image with SqueezeNet and print the top five ImageNet labels.

  The sample decodes the image with Pillow, and runtime.tensor_from_image
  stretches it to 224x224 and writes an ImageNet-normalized float32 NCHW RGB
  tensor on a CPU target. The model stage runs on the --device/--ep target,
  and the sample checks its placement after build. bind_input(0) binds the
  CPU tensor by position, run executes the stage, and softmax over
  output(0) picks the top five. The C++ sample uses its WIC adapter instead.

Run it
  python main.py [--device cpu|gpu|npu] [--ep <name>] [--verbose]

Learn more (paths relative to this file)
  ../../../../docs/Runtime/python-samples.md
  ../../../../docs/Runtime/tutorials/01-first-inference.md
  ../../../../docs/api-reference/CommonPatterns.md
"""

from __future__ import annotations

import argparse
from contextlib import ExitStack
from pathlib import Path
import sys

_RUNTIME_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_RUNTIME_ROOT / "python"))

from winml_runtime_samples.entrypoint import run_entry_point  # noqa: E402
from winml_runtime_samples.inference import (  # noqa: E402
    add_inference_arguments,
    enter_execution_targets,
    execution_request,
    import_runtime,
    require_files,
    validate_and_print_stage_placement,
    validate_tensor_desc,
)
from winml_runtime_samples.media import (  # noqa: E402
    load_labels,
    open_image,
    softmax,
    top_indices,
)


def _parse_args() -> argparse.Namespace:
    model_root = _RUNTIME_ROOT / "models" / "squeezenet"
    parser = argparse.ArgumentParser(
        description=(
            "Decode an image, create an ImageNet NCHW tensor, run SqueezeNet, "
            "and print the top five classes."
        )
    )
    parser.add_argument(
        "--model",
        type=Path,
        default=model_root / "SqueezeNet.onnx",
        help="SqueezeNet ONNX model.",
    )
    parser.add_argument(
        "--image",
        type=Path,
        default=model_root / "sample-image.jpg",
        help="Image to classify.",
    )
    parser.add_argument(
        "--labels",
        type=Path,
        default=model_root / "imagenet_classes.txt",
        help="ImageNet label file.",
    )
    add_inference_arguments(parser)
    return parser.parse_args()


def main() -> int:
    args = _parse_args()
    request = execution_request(args)
    require_files(
        {
            "model": args.model,
            "image": args.image,
            "labels": args.labels,
        }
    )

    runtime_api = import_runtime()
    print("=== Windows ML Runtime: Image classification ===\n")

    with ExitStack() as resources:
        runtime = resources.enter_context(runtime_api.Runtime())
        image_target = resources.enter_context(runtime.create_cpu_target())
        print("[1/5] Runtime and CPU image-tensor target created.")

        image = open_image(args.image)
        try:
            image_options = runtime_api.ImageTensorOptions(
                data_type=runtime_api.TensorDataType.FLOAT32,
                layout=runtime_api.TensorLayout.NCHW,
                channel_order=runtime_api.TensorChannelOrder.RGB,
                size=(224, 224),
                resize_mode=runtime_api.ImageResizeMode.STRETCH,
                normalization=runtime_api.TensorNormalization.imagenet(),
            )
            input_tensor = resources.enter_context(
                runtime.tensor_from_image(
                    image,
                    options=image_options,
                    target=image_target,
                )
            )
        finally:
            image.close()

        validate_tensor_desc(
            input_tensor.desc(),
            runtime_api.TensorDataType.FLOAT32,
            (1, 3, 224, 224),
            "Image",
        )
        print("[2/5] Image decoded, resized, and normalized to [1,3,224,224].")

        # enter_execution_targets retains the hardware target that backs a
        # provider-pinned stage target.
        stage_target, _tensor_target = enter_execution_targets(
            resources,
            runtime_api,
            runtime,
            request,
        )

        # The declared schema describes the model's inputs and outputs before
        # any stage is built; check it matches the tensor created above.
        source_model = resources.enter_context(runtime.load_model(str(args.model)))
        with source_model.schema() as source_schema:
            if source_schema.input_count < 1 or source_schema.output_count < 1:
                raise RuntimeError("SqueezeNet must expose input and output ordinal 0.")
            model_input_type, model_input_shape = source_schema.input_desc(0)
        if (
            model_input_type != runtime_api.TensorDataType.FLOAT32
            or model_input_shape != (1, 3, 224, 224)
        ):
            raise RuntimeError(
                "SqueezeNet input ordinal 0 shape mismatch: "
                f"{model_input_type.name} {model_input_shape}"
            )
        print(f"[3/5] Model ready for {request.device.upper()}.")

        builder = resources.enter_context(runtime.create_pipeline_builder())
        # A stage places one model on one execution target. Build materializes
        # the stage and validates that the model can run on that target.
        stage = resources.enter_context(
            builder.add_model_stage(source_model, stage_target, "squeezenet")
        )
        # The Runtime publishes only outputs that are requested before the
        # pipeline is built.
        stage.request_output(0)
        pipeline = resources.enter_context(builder.build())
        validate_and_print_stage_placement(stage, request, args.verbose)
        print("[4/5] One-stage pipeline built.")

        # Bindings are positional (ordinal 0), not ONNX tensor names. The CPU
        # input tensor can feed a stage on any target.
        stage.bind_input(0, input_tensor)
        pipeline.run()
        output = resources.enter_context(stage.output(0))
        output_type, _output_shape = output.desc()
        if output_type != runtime_api.TensorDataType.FLOAT32:
            raise RuntimeError(
                f"SqueezeNet output ordinal 0 must be FLOAT32, not {output_type.name}."
            )
        probabilities = softmax(output.to_numpy())
        labels = load_labels(args.labels)
        if len(labels) < probabilities.size:
            raise RuntimeError(
                f"Label file has {len(labels)} entries for "
                f"{probabilities.size} model classes."
            )

        print("[5/5] Inference complete.\n")
        print("Top-5 predictions:")
        for rank, index in enumerate(top_indices(probabilities, 5), start=1):
            print(f"  #{rank}  {probabilities[index] * 100:6.2f}%  {labels[index]}")

    return 0


if __name__ == "__main__":
    run_entry_point(main)

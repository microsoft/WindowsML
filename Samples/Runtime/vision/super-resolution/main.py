# Copyright (C) Microsoft Corporation. All rights reserved.

"""Super-resolution: upscale one image with Windows ML Runtime.

Upscale one image 2x with SESR and save the 512x512 result as a PNG.

  The sample decodes the image with Pillow, and runtime.tensor_from_image
  stretches it into a (1, 3, 256, 256) float32 RGB tensor in the 0-255
  range on a CPU target. One stage on the --device/--ep target takes it
  through bind_input(0) and run. to_image_bytes turns output(0) into packed
  RGBA, and the sample's own PNG writer saves it. The C++ sample starts
  from a Media Foundation NV12 frame instead.

Run it
  python main.py [--model <path>] [--image <path>] [--output <path>]
                 [--device cpu|gpu|npu] [--ep <name>]
                 [--performance|--efficiency] [--verbose]

Learn more (paths relative to this file)
  README.md
  ../../../../docs/Runtime/python-samples.md
  ../../../../docs/Runtime/tutorials/02-tensors-and-media.md
  ../../../../docs/Runtime/tutorials/05-accelerators.md
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
    open_image,
    save_rgba_png,
)


def _parse_args() -> argparse.Namespace:
    model_root = _RUNTIME_ROOT / "models" / "sesr_x2"
    parser = argparse.ArgumentParser(
        description=(
            "Decode an image, create the SESR RGB tensor, run 2x "
            "super-resolution, and save a PNG."
        )
    )
    parser.add_argument(
        "--model",
        type=Path,
        default=model_root / "sesr_x2.onnx",
        help="Prepared SESR x2 ONNX model.",
    )
    parser.add_argument(
        "--image",
        type=Path,
        default=model_root / "sample-image.jpg",
        help="Source image.",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("super-resolution-output.png"),
        help="Output PNG path.",
    )
    add_inference_arguments(parser)
    return parser.parse_args()


def main() -> int:
    args = _parse_args()
    request = execution_request(args)
    require_files({"model": args.model, "image": args.image})

    runtime_api = import_runtime()
    print("=== Windows ML Runtime: Super-resolution ===\n")

    with ExitStack() as resources:
        # The runtime creates targets, models, and builders. The image adapter
        # allocates its tensor on the target supplied here, so this path uses CPU.
        runtime = resources.enter_context(runtime_api.Runtime())
        image_target = resources.enter_context(runtime.create_cpu_target())
        print("[1/5] Runtime and CPU image-tensor target created.")

        image = open_image(args.image)
        try:
            input_options = runtime_api.ImageTensorOptions(
                data_type=runtime_api.TensorDataType.FLOAT32,
                layout=runtime_api.TensorLayout.NCHW,
                channel_order=runtime_api.TensorChannelOrder.RGB,
                size=(256, 256),
                resize_mode=runtime_api.ImageResizeMode.STRETCH,
                normalization=runtime_api.TensorNormalization.identity(),
            )
            # tensor_from_image performs resize, channel order, layout, and
            # normalization according to ImageTensorOptions.
            input_tensor = resources.enter_context(
                runtime.tensor_from_image(
                    image,
                    options=input_options,
                    target=image_target,
                )
            )
        finally:
            image.close()

        validate_tensor_desc(
            input_tensor.desc(),
            runtime_api.TensorDataType.FLOAT32,
            (1, 3, 256, 256),
            "SESR input",
        )
        print("[2/5] Image tensorized as RGB float32 [1,3,256,256] in [0,255].")

        # enter_execution_targets (python/winml_runtime_samples/inference.py)
        # creates and retains both the stage target and tensor target.
        stage_target, inference_tensor_target = enter_execution_targets(
            resources,
            runtime_api,
            runtime,
            request,
        )

        # The declared schema describes the model's ordinal inputs and outputs
        # before a stage is built; check it matches the tensor created above.
        source_model = resources.enter_context(runtime.load_model(str(args.model)))
        with source_model.schema() as source_schema:
            if source_schema.input_count < 1 or source_schema.output_count < 1:
                raise RuntimeError("SESR must expose input and output ordinal 0.")
            model_input_type, model_input_shape = source_schema.input_desc(0)
            model_output_type, model_output_shape = source_schema.output_desc(0)
        if (
            model_input_type != runtime_api.TensorDataType.FLOAT32
            or model_input_shape != (1, 3, 256, 256)
            or model_output_type != runtime_api.TensorDataType.FLOAT32
            or model_output_shape != (1, 3, 512, 512)
        ):
            raise RuntimeError(
                "SESR model shape mismatch: "
                f"input={model_input_type.name} {model_input_shape}, "
                f"output={model_output_type.name} {model_output_shape}"
            )
        print(f"[3/5] SESR model ready for {request.device.upper()}.")

        # A stage places one model on one execution target. Build materializes
        # the stage and validates that the model can run on that target.
        builder = resources.enter_context(runtime.create_pipeline_builder())
        stage = resources.enter_context(
            builder.add_model_stage(source_model, stage_target, "sesr-x2")
        )
        # The Runtime publishes only outputs that are requested before the
        # pipeline is built.
        stage.request_output(0)
        pipeline = resources.enter_context(builder.build())
        validate_and_print_stage_placement(stage, request, args.verbose)
        print("[4/5] One-stage pipeline built.")

        # Bindings are positional (ordinal 0), not ONNX tensor names.
        stage.bind_input(0, input_tensor)
        pipeline.run()
        output_tensor = resources.enter_context(stage.output(0))
        validate_tensor_desc(
            output_tensor.desc(),
            runtime_api.TensorDataType.FLOAT32,
            (1, 3, 512, 512),
            "SESR output",
        )
        output_height, output_width = 512, 512

        output_options = runtime_api.ImageTensorOptions(
            data_type=runtime_api.TensorDataType.FLOAT32,
            layout=runtime_api.TensorLayout.NCHW,
            channel_order=runtime_api.TensorChannelOrder.RGB,
            size=(output_width, output_height),
            normalization=runtime_api.TensorNormalization.identity(),
        )
        # to_image_bytes reads the tensor using the supplied image layout and
        # produces packed RGBA bytes for the small PNG writer below.
        rgba = output_tensor.to_image_bytes(
            size=(output_width, output_height),
            image_format=runtime_api.ImageFormat.RGBA8,
            options=output_options,
            target=inference_tensor_target,
        )
        saved_path = save_rgba_png(
            rgba,
            (output_width, output_height),
            args.output,
        )
        print(
            f"[5/5] Upscaled 256x256 to {output_width}x{output_height} "
            f"and saved {saved_path}"
        )

    return 0


if __name__ == "__main__":
    run_entry_point(main)

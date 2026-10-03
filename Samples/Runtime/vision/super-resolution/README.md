<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Super-resolution

This sample converts an NV12 video frame into the RGB `[0,255]` tensor expected
by SESR, runs 2x super-resolution, and converts the model output back to NV12.
It creates the input frame from a sample image so the inference path does not
require a camera.

## API path

1. Create a CPU tensorization target.
2. Convert NV12 to an NCHW RGB tensor with the Media Foundation adapter.
3. Build a one-stage SESR pipeline on the selected inference target.
4. Run inference and keep the output in Runtime-managed storage.
5. Convert the output tensor back to an `IMFSample`.

## Run

From `Samples\Runtime`:

```powershell
.\check_artifacts.ps1 -Sample super-resolution
.\build.ps1 -Configuration Release -PackageOnly

.\run_super_resolution.ps1 -Device cpu
```

The Python sample uses the same SESR RGB float32
`[1,3,256,256] -> [1,3,512,512]` tensor shapes. After
[setting up Python](../../../../docs/Runtime/python-samples.md#set-up-python), run:

```powershell
python.exe .\vision\super-resolution\main.py --device cpu
```

Python uses Pillow to decode the JPEG, the Runtime image-memory functions for
the model tensor and the packed RGBA output, and a small standard-library PNG
encoder. It skips the C++ sample's Media Foundation NV12 conversion, which does
not change the model tensor shape.

The sample saves `super-resolution-output.png`. Use `--output <path>` to choose
another output location.

The source is the pinned JPEG at `models\sesr_x2\sample-image.jpg`. It is
decoded and resized to 256x256 in memory; the sample does not save that
intermediate image. PNG files produced by the sample are 512x512 outputs.

To run the same sample on another available target, add the device and
provider options from [Devices and execution providers](../../../../docs/Runtime/providers.md).

The upstream SESR export contains one no-op `Identity` node. Model acquisition
removes only no-op Identity nodes after verifying the pinned download, and the
Runtime then consumes that prepared `sesr_x2.onnx`. Preparation uses an
existing Python environment with `onnx==1.22.0`, or creates a local tool
environment for that dependency.

For a guided data-flow walkthrough, see
[Tutorial 2: tensors and Windows media](../../../../docs/Runtime/tutorials/02-tensors-and-media.md).

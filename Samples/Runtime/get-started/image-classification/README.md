<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Image classification

This is the smallest complete Runtime sample. It converts an image into the
normalized NCHW tensor SqueezeNet expects, builds a one-stage pipeline, runs it,
and reads the highest-scoring class.

## API path

1. Create `IWinMLRuntime` and a CPU execution target for the image tensor.
2. Use the WIC tensor adapter to decode, resize, and normalize the sample image
   into a `[1,3,224,224]` tensor.
3. Create the model stage's execution target and load `SqueezeNet.onnx`.
4. Add one model stage, request its first output, call the builder's one-shot
   `Build`, and query the target the stage resolved to.
5. Bind the input by ordinal, run, and read the output tensor.

## Run

From `Samples\Runtime`:

```powershell
.\check_artifacts.ps1 -Sample image-classification
.\build.ps1 -Configuration Release -PackageOnly

.\run_image_classification.ps1 -Device cpu
```

The Python sample follows the same five steps. It decodes the image with Pillow,
converts it to a tensor with `Runtime.tensor_from_image`, and checks the model's
declared input schema before it builds the pipeline. After
[setting up Python](../../../../docs/Runtime/python-samples.md#set-up-python), run:

```powershell
python.exe .\get-started\image-classification\main.py --device cpu
```

To run the same sample on another available target, add the device and
provider options from [Devices and execution providers](../../../../docs/Runtime/providers.md).
The included SqueezeNet model is quantized, and some providers decline its
quantized operators. ONNX Runtime then runs those operators on the CPU, and
`-Diagnostics` shows where each node runs.

For a guided code walkthrough, see
[Tutorial 1: your first Runtime inference](../../../../docs/Runtime/tutorials/01-first-inference.md).

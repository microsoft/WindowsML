<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Tutorial 1: your first Runtime inference

This tutorial runs SqueezeNet on CPU and traces the smallest complete Runtime
lifecycle.

## 1. Acquire the assets

```powershell
.\check_artifacts.ps1 -Sample image-classification
```

This reports the files needed under `models\squeezenet`.
Nothing is downloaded for you. When files are missing, the script prints the
download command for each one, including the source it comes from. Review the
publisher's terms, run the printed commands, then run the check again to confirm
every file matches the catalog.

## 2. Build the project

```powershell
.\build.ps1 -Configuration Release -PackageOnly `
  -Sample image-classification
```

## 3. Run it

```powershell
.\run_image_classification.ps1 -Device cpu
```

The sample prints the five most likely ImageNet labels for the included dog
photo. The top label scores about 97%.

## 4. Trace the code

Open `get-started\image-classification\main.cpp` and follow:

1. `WinMLCreateRuntime` creates `IWinMLRuntime`, and
   `CreateCpuExecutionTarget` creates the target for the image tensor.
2. The WIC adapter creates a normalized `[1,3,224,224]` tensor.
3. `LoadModelFromFile` returns an `IWinMLModel`.
4. `CreatePipelineBuilder` creates a one-shot builder.
5. `AddModelStage` places the model on an execution target.
6. `Build` materializes the stage and validates compatibility.
7. `GetExecutionTarget` reports the target the stage resolved to.
8. `BindInput` uses positional input index `0`.
9. `Run` executes the pipeline.
10. `GetOutput` returns the output tensor.
11. A synchronized CPU read produces the class scores.

## 5. Important invariant

Runtime binding identity is positional. ONNX names are optional ONNX Runtime
metadata; stage binding uses input/output ordinals.

## 6. Try a controlled failure

Run with a GPU request:

```powershell
.\run_image_classification.ps1 -Device gpu -Diagnostics
```

If the machine has no hardware GPU, target creation fails instead of silently
running on CPU. That is expected and makes a device request measurable.

## Next

Continue to [Tensors and media](02-tensors-and-media.md).

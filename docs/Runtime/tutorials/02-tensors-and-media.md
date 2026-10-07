<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Tutorial 2: tensors and Windows media

This tutorial compares the image-classification WIC adapter with the
super-resolution Media Foundation path.

## 1. Prepare and build

```powershell
.\check_artifacts.ps1 -Sample super-resolution
.\build.ps1 -Configuration Release -PackageOnly `
  -Sample super-resolution
```

If files are missing, the script prints the commands that acquire them. Review
the publisher's terms, run the commands, then run the check again. See
[Models and artifacts](../artifacts.md) for details.

## 2. Run on CPU

```powershell
.\run_super_resolution.ps1 `
  -Device cpu `
  -Output .\super-resolution-output.png
```

Expected result:

- a 512x512 PNG;
- console output describing the source frame, tensor shape, inference target,
  and output conversion.

The source asset is `models\sesr_x2\sample-image.jpg` (a larger JPEG). The
sample creates the 256x256 model input in memory, so there is no 256x256 input
PNG on disk.

## 3. Trace the data flow

Read `vision\super-resolution\main.cpp`:

```text
JPEG
 -> WIC decode
 -> synthetic NV12 IMFSample
 -> Media Foundation tensor adapter
 -> [1,3,256,256] RGB tensor
 -> SESR Runtime stage
 -> output tensor
 -> NV12 IMFSample
 -> PNG
```

## 4. Model placement versus tensor residency

These are different concepts:

- Model placement selects where the Runtime stage executes.
- Tensor residency describes where a tensor is stored when it crosses a
  CPU/device boundary.

This sample tensorizes on CPU and can execute the model on another target.
Runtime handles the required transfer.

## 5. Compare with image classification

| Concept | Image classification | Super-resolution |
|---|---|---|
| Input API | WIC bitmap adapter | Media Foundation sample adapter |
| Model count | One | One |
| Input format | RGB image | NV12 video frame |
| Output | Class scores | Image tensor converted to NV12 |
| Main lesson | Smallest lifecycle | Platform media integration |

## Next

Continue to [Language models](03-language-models.md).

<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Image classification in C#

This is the C# version of the [image classification sample](../README.md). It
follows the same steps: turn an image into the normalized NCHW tensor
SqueezeNet expects, build a one-stage pipeline on the requested target, run
it, and print the most likely ImageNet labels. It takes the same device and
provider options as the C++ sample and prints the same predictions.

## How the bindings are generated

The sample declares no Runtime types of its own. The
`Microsoft.Windows.AI.MachineLearning` package includes Win32 metadata for the
Runtime API. When a C# project also references
[CsWin32](https://github.com/microsoft/CsWin32), the package passes that
metadata to CsWin32, which generates bindings for the APIs listed in
`NativeMethods.txt` when the project builds.

- The project imports
  [`WinMLSamples.CSharp.props`](../../../shared/csharp/WinMLSamples.CSharp.props),
  which the C# samples share. It references the package and CsWin32, sets the
  runtime identifier, and compiles the shared helpers in
  [`shared/csharp`](../../../shared/csharp/).
- CsWin32 combines every `NativeMethods.txt`. The
  [shared list](../../../shared/csharp/NativeMethods.txt) covers the APIs the
  helpers use, and [this sample's list](NativeMethods.txt) adds
  `IWinMLImageTensorFactory` and the WIC APIs. Runtime APIs are listed by their
  full names, because the Windows SDK metadata also defines legacy WinML types
  with the same short names. Interfaces obtained through `QueryInterface` are
  listed explicitly.
- [`NativeMethods.json`](../../../shared/csharp/NativeMethods.json) turns off
  marshaling. The Runtime interfaces are `local` COM interfaces, so CsWin32
  generates them as unmanaged structs that the sample calls through pointers.
- Generated interface methods throw an exception for a failure `HRESULT` and
  return their `[out, retval]` value. Factory functions such as
  `WinMLCreateRuntime` return the `HRESULT`.

The runtime identifier copies the package's native DLLs next to the
executable, where the Runtime loads its dependencies from.

The C++ sample uses the header-only WIC tensor adapter to decode the image. That
helper is not part of the Runtime API, so this sample makes the same WIC calls
through CsWin32 and passes the 32-bit BGRA pixels to
`IWinMLImageTensorFactory::CreateTensorFromImageMemory`. Both samples therefore
build the same input tensor. SqueezeNet's scores are whole numbers, so several
labels can tie. Both samples list tied labels in class-index order, which keeps
their top-5 lists identical.

## Devices and providers

The shared helpers implement the options the same way as the C++ helpers:

| Option | What the sample does |
|---|---|
| `--device cpu\|gpu\|npu` | Creates the stage's target. GPU and NPU targets come from the DXCore adapter that the helpers rank, through `IWinMLRuntime::CreateExecutionTargetFromAdapter`. |
| `--ep <name>` | Finds the provider in the Windows ML execution-provider catalog (`ExecutionProviderCatalog`), makes it ready, registers its library with ONNX Runtime, and pins it through `IWinMLOrtCompatibility::CreateExecutionTarget`. Short aliases such as `openvino` and `qnn` work. `--device npu` requires it. |
| `--performance`, `--efficiency` | Prefer the adapter with the most dedicated memory, or an integrated adapter. |
| `--verbose` | Raises ONNX Runtime logging to show node placement, and prints the stage's `IWinMLStageExecutionDiagnostics`: coverage, work shares by device and execution technology, and the requested and selected provider. |

The catalog and the ONNX Runtime C# API come from the managed assemblies in the
same package, so `--ep` needs no other package.

## Requirements

- The .NET 8 SDK or later.
- A `Microsoft.Windows.AI.MachineLearning` package that contains
  `metadata\win32\Microsoft.Windows.AI.MachineLearning.Runtime.winmd`. The
  version pinned in `Samples\Directory.Packages.props` does.

## Build and run

From `Samples\Runtime`:

```powershell
.\check_artifacts.ps1 -Sample image-classification
.\build.ps1 -Sample image-classification-csharp -Configuration Release `
  -Platform x64 -PackageOnly

.\run_image_classification.ps1 -Language csharp -Device cpu
.\run_image_classification.ps1 -Language csharp -Device gpu -Diagnostics
.\run_image_classification.ps1 -Language csharp -Device npu -Ep qnn
```

Use `-Platform ARM64` on an Arm64 device. The platform selects the .NET runtime
identifier (`win-x64` or `win-arm64`) and therefore which native Windows ML
DLLs are copied next to the sample, so it must match the architecture the
process runs as. An x64 build also runs on Arm64 devices under emulation.

The executable also accepts `--model`, `--image`, and `--labels` to classify
another image or run another ImageNet classifier with the same input layout.

To call more of the Runtime API, add its full name to `NativeMethods.txt`.

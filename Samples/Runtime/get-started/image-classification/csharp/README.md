<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Image classification in C#

This is the C# version of the [image classification sample](../README.md). It
follows the same steps: turn an image into the normalized NCHW tensor
SqueezeNet expects, build a one-stage pipeline on the requested target, run
it, and print the most likely ImageNet labels. It takes the same device and
provider options as the C++ sample and prints the same predictions.

## How the sample calls the Runtime

The sample calls the Runtime through the C# sample wrapper in
[`shared/csharp/Runtime`](../../../shared/csharp/Runtime/), which gives the
Runtime's COM-style C API an idiomatic C# surface. The sample code has no
pointers, `unsafe` blocks, or `HRESULT` checks:

- Runtime objects such as `WinMLRuntime`, `ExecutionTarget`, `Pipeline`,
  `Stage`, and `Tensor` are `IDisposable`, and `using` releases each one when
  the sample is done with it.
- A failure throws a .NET exception whose `HResult` is the Runtime's
  `HRESULT`. Like the C++ sample, the sample exits with that `HRESULT`.
- The output is read through `Tensor.Lock<float>`, whose span softmax reads
  directly. On a CPU target that span is the tensor's memory; a GPU or NPU
  output is a staged CPU copy.

The wrapper is sample code, not a supported API. The
[shared README](../../../shared/csharp/README.md) explains why it exists and
what it covers.

The C++ sample uses the header-only WIC tensor adapter to decode the image.
That helper is not part of the Runtime API, so this sample decodes the image
with `Windows.Graphics.Imaging`, which uses the same WIC codecs, and passes the
32-bit BGRA pixels to `ImageTensorFactory.CreateTensorFromImageMemory`. Both
samples ignore orientation metadata and color profiles, so they build the same
input tensor. SqueezeNet's scores are whole numbers, so several labels can
tie. Both samples list tied labels in class-index order, which keeps their
top-5 lists identical.

## Project settings

- The project imports
  [`WinMLSamples.CSharp.props`](../../../shared/csharp/WinMLSamples.CSharp.props),
  which the C# samples share. It references the package and CsWin32, sets the
  runtime identifier, and compiles the wrapper and the shared helpers in
  [`shared/csharp`](../../../shared/csharp/).
- The wrapper calls bindings that CsWin32 generates from the Win32 metadata in
  the `Microsoft.Windows.AI.MachineLearning` package, for the APIs listed in
  the [shared `NativeMethods.txt`](../../../shared/csharp/NativeMethods.txt).

The runtime identifier copies the package's native DLLs next to the
executable, where the Runtime loads its dependencies from.

## Devices and providers

The shared helpers implement the options the same way as the C++ helpers:

| Option | What the sample does |
|---|---|
| `--device cpu\|gpu\|npu` | Creates the stage's target. GPU and NPU targets come from the DXCore adapter that `DXCoreAdapter.FindBest` ranks, through `WinMLRuntime.CreateExecutionTarget(DXCoreAdapter)`. |
| `--ep <name>` | Finds the provider in the Windows ML execution-provider catalog (`ExecutionProviderCatalog`), makes it ready, registers its library with ONNX Runtime, and pins it through `WinMLRuntime.CreateExecutionTarget(providerName, kind, hardwareTarget)`. Short aliases such as `openvino` and `qnn` work. `--device npu` requires it. |
| `--performance`, `--efficiency` | Prefer the adapter with the most dedicated memory, or an integrated adapter. |
| `--verbose` | Raises ONNX Runtime logging to show node placement, and prints the stage's `StageExecutionDiagnostics`: coverage, work shares by device and execution technology, and the requested and selected provider. |

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

To call a Runtime API that the wrapper does not cover yet, see
[the shared README](../../../shared/csharp/README.md#the-sample-wrapper).

<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# ONNX Runtime with the Windows ML CG Execution Provider

This sample keeps inference code on the ONNX Runtime C++ API while
`WinMLCGExecutionProvider` adapts execution to the Windows ML Runtime and
DirectX Compute Graph Compiler stack.

The `Microsoft.Windows.AI.MachineLearning` NuGet package deploys ONNX Runtime,
the execution provider, and its native dependencies beside the application.
No separate execution-provider installation is required.

## What the sample does

1. Registers the package-deployed Windows ML CG Execution Provider.
2. Selects a physical D3D12 GPU exposed by the provider.
3. JIT-compiles SqueezeNet ONNX to an application-owned EPContext cache when a
   matching cache does not exist.
4. Loads the cache through ONNX Runtime and classifies the sample image.
5. Reuses the cache on later launches.

The cache identity includes the source-model hash, Runtime package, processor
architecture, GPU, and compilation settings. `--recompile` replaces only the
cache for the current identity.

Concurrent first launches, including those in different Windows logon sessions,
serialize cache publication through an exclusively opened per-cache `.lock` file.
The file stays on disk; ownership lasts only while its handle is open, and cache
cleanup does not remove it. Waiting for ownership times out after five minutes.
The sample compiles to unique temporary files and publishes the manifest last
as the readiness marker.
If a process stops between those steps, the next launch removes the incomplete
publication and rebuilds it automatically.

## Build and run

Requires Visual Studio with Desktop development with C++, a Windows SDK, and
a physical GPU with a CGC-capable driver. Restore uses the Runtime package
version pinned for all samples in `Samples\Directory.Packages.props`.
Package sources are configured in `Samples\Runtime\NuGet.config`: nuget.org
and the existing `localpackages` override directory.
No Runtime source build is required.

This sample reuses the Runtime samples' shared build settings, C++ helpers,
artifact catalog, and executable launcher.

From `Samples\Runtime` in PowerShell:

```powershell
.\check_artifacts.ps1 -Sample onnx-runtime-winmlcgep
# Review and run any acquisition commands printed by the check, then check again.
.\build.ps1 -Sample onnx-runtime-winmlcgep -Platform x64 -Configuration Release -PackageOnly
.\run_onnx_runtime_winmlcgep.ps1 -Platform x64 -Configuration Release
```

Use `-Platform ARM64` for an ARM64 build. Outputs follow the shared layout:
`out\<platform>\<configuration>\onnx-runtime-winmlcgep`. Model assets are
cataloged under `models\squeezenet`; caches stay beside the executable.
The artifact check and run script verify pinned sizes and SHA-256 hashes and
reject missing or altered files. Neither downloads or modifies model assets.
See [Models and artifacts](../../../../docs/Runtime/artifacts.md) for acquisition guidance.
Builds never download models.

To use a separate model root, pass `-ModelsDir` to the run script:

```powershell
.\run_onnx_runtime_winmlcgep.ps1 -ModelsDir "D:\sample-assets\models"
```

The script verifies that root and forwards it as `--models-dir` to the executable.
All three assets must be under its `squeezenet` subdirectory; executable-local
copies cannot override them. An explicit root never falls back to asset discovery.
Direct executable launches without `--models-dir` retain the shared search behavior.

The first run prints `Cache: cold compile`; later runs print `Cache: warm`.
Both runs must report a physical adapter, WinMLCGEP provider assignment, and
`Prediction: Samoyed`.
Interactive console runs then wait for any key before exiting; redirected
runs exit without waiting.

On a multi-GPU system, select one adapter by description instead of relying on
enumeration order:

```powershell
.\run_onnx_runtime_winmlcgep.ps1 `
  -AdapterName "NVIDIA GeForce RTX 3080 Laptop GPU"
```

To rebuild the cache:

```powershell
.\run_onnx_runtime_winmlcgep.ps1 -Recompile
```

On a multi-GPU system, the sample lists physical candidates and accepts
`-AdapterIndex N`. The executable accepts the corresponding `--adapter-name`,
`--adapter-index`, `--models-dir`, and `--recompile` options directly.

## API boundary

This is an ONNX Runtime execution-provider sample. It creates `Ort::Env`,
`Ort::SessionOptions`, and `Ort::Session` directly.

The native Runtime samples in this branch use `IWinMLModelCompiler` and Runtime pipelines.
Both paths reach the DirectX CGC implementation, but they preserve different
application API contracts.

WinMLCGEP currently requires file-backed ONNX for JIT compilation. The generated
cache is specific to the model, package, architecture, GPU, and compiler
configuration; do not copy it between unrelated targets.

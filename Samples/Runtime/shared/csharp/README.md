<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Shared C# helpers

The C# samples in `Samples\Runtime` and `Samples\Tasks` import
[`WinMLSamples.CSharp.props`](WinMLSamples.CSharp.props). It targets .NET 8,
references the `Microsoft.Windows.AI.MachineLearning` package and
[CsWin32](https://github.com/microsoft/CsWin32), sets the runtime identifier
that copies the native Windows ML DLLs next to the executable, and compiles the
helpers in this folder into each sample.

| File | Purpose |
|---|---|
| `NativeMethods.txt` | Runtime, DXCore, and COM APIs the helpers use. CsWin32 combines it with each sample's own `NativeMethods.txt`. |
| `NativeMethods.json` | Generates unmanaged interface structs (`allowMarshaling: false`) and keeps the `HRESULT` of methods whose success codes the helpers read. |
| `ComObjects.cs` | Releases owned interface pointers in reverse order and wraps `QueryInterface`. |
| `DeviceOptions.cs` | Parses `--device`, `--ep`, `--performance`, `--efficiency`, and `--verbose`. |
| `ExecutionProviders.cs` | Prepares and registers an `--ep` provider from the Windows ML execution-provider catalog and enables verbose ONNX Runtime logging. Matches `ep_catalog_utils.h`. |
| `ExecutionTargets.cs` | Creates CPU, GPU, and NPU targets from ranked DXCore adapters, and provider-pinned targets through `IWinMLOrtCompatibility`. Matches `execution_target_utils.h`. |
| `StageDiagnostics.cs` | Enables and prints `IWinMLStageExecutionDiagnostics` and `IWinMLOrtStageDiagnostics` for a stage. |
| `SampleAssets.cs` | Finds files under the catalog's `models` directory. |

The samples require CsWin32 0.3.369 or later. Earlier versions generate
`IUnknown*` parameters that the Runtime metadata takes from the Windows SDK
metadata, such as the adapter that `IWinMLRuntime::CreateExecutionTargetFromAdapter`
takes, as `IUnknown` values instead of pointers
([microsoft/CsWin32#1874](https://github.com/microsoft/CsWin32/issues/1874)).

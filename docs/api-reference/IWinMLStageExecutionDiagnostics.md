<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLStageExecutionDiagnostics

> Part of the [WinML Runtime API Reference](README.md).

Read-only diagnostics that describe where a built stage's work runs. Query any
[`IWinMLStage`](IWinMLStage.md) for this interface, including ONNX Runtime,
CGC, llama.cpp, and processor stages.

```
IWinMLStageExecutionDiagnostics  IID: 5fd646ac-5f56-42fb-9ea5-cee1c39feb22
IWinMLStageWorkShare             IID: d6b538b8-2cd8-4875-8647-5ca583f4bf0c
```

Diagnostics are off by default because capturing them adds work to `Build` and
can keep a placement record for the lifetime of the stage's execution
resources. Call `SetExecutionDiagnosticsEnabled(TRUE)` before `Build`; it returns
`E_NOT_VALID_STATE` after `Build` and `HRESULT_FROM_WIN32(ERROR_BUSY)` during
`Build`. The query methods succeed only while the stage belongs to a built
pipeline whose `Build` captured its diagnostics, and return `E_NOT_VALID_STATE`
otherwise. Values do not change after `Build`. Methods may be called from any
thread. Collecting diagnostics never fails `Build`.

## Coverage and work shares

`GetExecutionTarget` says which class of device was chosen. Coverage says how
much of the stage's work runs on that device:

| `WINML_STAGE_PLACEMENT_COVERAGE` | Meaning |
|---|---|
| `FULL` | All of the stage's work runs on the device of its execution target. |
| `PARTIAL` | Some of it runs there; the rest runs on another device, typically the host. |
| `NONE` | None of it runs there. |
| `UNKNOWN` | The distribution could not be determined, including when the adapter that runs some work cannot be identified. |

Work shares break coverage down. Each `IWinMLStageWorkShare` is the part of the
stage's work that runs on one device with one execution technology, an
immutable snapshot that stays valid on its own:

| Method | Returns |
|---|---|
| `GetDeviceKind` | `WINML_EXECUTION_TARGET_KIND`: CPU for work on the host, including a software adapter. |
| `GetAdapterLuid` | The adapter's LUID, or `hasLuid = FALSE` for native host code or an adapter that cannot be identified. |
| `GetImplementation` | `WINML_STAGE_WORK_IMPLEMENTATION`: `CPU`, `D3D12_DRIVER`, `D3D12_SHADER`, `DIRECTML`, `WEBGPU`, `VENDOR_LIBRARY`, or `UNKNOWN`. Treat an unrecognized value as `UNKNOWN`. |
| `GetUnitCount` | The number of units in the share, in the stage's work unit. |

`GetWorkUnit` reports the unit of every share of the stage, and is `UNKNOWN`
exactly when the stage reports no shares:

| Stage | `WINML_STAGE_WORK_UNIT` | Shares |
|---|---|---|
| ONNX Runtime | `GRAPH_NODE` | Graph nodes per execution provider and device. |
| CGC | `COMPILED_SUBGRAPH` | Dispatches of compiled subgraphs per execution technology, such as `D3D12_DRIVER` for work the driver compiles and `DIRECTML` for work that runs through DirectML. |
| llama.cpp | `MODEL_LAYER` | Layers offloaded to the accelerator, and the layers left on the host. |
| Processor | `STAGE` | The processor, on the host or on its adapter. |

`GetWorkShareCount` and `GetWorkShare(index)` enumerate the shares in descending
unit count; `GetWorkShare` returns `E_BOUNDS` for an index out of range. Unit
counts compare shares of one stage and are not a measure of cost.
`GetPlacementDescription` returns a `CoTaskMemAlloc` string for logging only;
do not parse it.

```cpp
wil::com_ptr<IWinMLStageExecutionDiagnostics> diagnostics;
THROW_IF_FAILED(stage->QueryInterface(IID_PPV_ARGS(diagnostics.put())));
THROW_IF_FAILED(diagnostics->SetExecutionDiagnosticsEnabled(TRUE));
THROW_IF_FAILED(builder->Build(pipeline.put()));

WINML_STAGE_PLACEMENT_COVERAGE coverage{};
THROW_IF_FAILED(diagnostics->GetPlacementCoverage(&coverage));
if (coverage != WINML_STAGE_PLACEMENT_COVERAGE_FULL)
{
    // Some of the stage's work runs on another device.
}
```

The C++ helpers `TryGetStageExecutionDiagnostics` and `GetStageWorkShares` in
`winml/runtime/utils/WinMLRuntimeRAII.h` return the shares as a vector. In
Python, set `stage.execution_diagnostics_enabled = True` before `build()` and
read `stage.execution_diagnostics.coverage`, `.work_unit`, `.description`, and
`.work_shares`.

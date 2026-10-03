<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLExecutionTarget

> Part of the [WinML Runtime API Reference](README.md).

**Execution placement object.**

Create an execution target with `IWinMLRuntime::CreateCpuExecutionTarget`, `CreateExecutionTarget`, `CreateExecutionTargetFromAdapter`, `CreateExecutionTargetFromD3D12`, or [`IWinMLOrtCompatibility`](IWinMLOrtCompatibility.md). Passing a null target to `IWinMLPipelineBuilder::AddModelStage` leaves placement automatic for that stage.

D3D12-backed targets also implement [`IWinMLD3D12ExecutionTarget`](IWinMLD3D12ExecutionTarget.md). Target-bound tensor factory interfaces are obtained with `QueryInterface` from the target object.

```
IID: 21a4ed3a-0ab3-441c-b9d8-f7d962001774
```

## `GetKind`

```cpp
HRESULT GetKind([out, retval] WINML_EXECUTION_TARGET_KIND* kind);
```

Returns the hardware class represented by this target.

| Parameter | Description |
|---|---|
| `kind` | Receives `WINML_EXECUTION_TARGET_KIND_CPU`, `WINML_EXECUTION_TARGET_KIND_GPU`, or `WINML_EXECUTION_TARGET_KIND_NPU`. |

**Returns:** `S_OK` on success. Returns `E_POINTER` when `kind` is null.

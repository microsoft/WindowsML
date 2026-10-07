<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLPipeline

> Part of the [WinML Runtime API Reference](README.md).

Executable pipeline returned by `IWinMLPipelineBuilder::Build`. Bind inputs and
published outputs on the stage handles, then run the pipeline.

```
IID: c22e40c2-4e8a-4403-bf98-a0d50ac099c9
```

## `Run`

```cpp
HRESULT Run();
```

Executes one synchronous pipeline step.

**Returns:** `S_OK` on success. If another `Run`, `Submit`, reset, or state
mutation is active on the same pipeline, returns `HRESULT_FROM_WIN32(ERROR_BUSY)`.
Invalid built-pipeline state returns `E_NOT_VALID_STATE`; binding and backend
failures are returned from the underlying validation or execution path.

---

## `ResetExecutionState`

```cpp
HRESULT ResetExecutionState();
```

Resets runtime-managed execution state. This clears retained next-iteration
values and asks stateful backends to reset without changing stage input bindings
or rebuilding the pipeline.

**Returns:** `S_OK` on success, `HRESULT_FROM_WIN32(ERROR_BUSY)` if execution or
another state mutation is active, or a backend reset failure.

### Example

```cpp
THROW_IF_FAILED(pipeline->Run());
THROW_IF_FAILED(pipeline->ResetExecutionState());
THROW_IF_FAILED(pipeline->Run());
```

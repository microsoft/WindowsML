<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLPipelineExecution

> Part of the [WinML Runtime API Reference](README.md).

Backend-specific asynchronous execution extension for a built pipeline. Query it
from `IWinMLPipeline`. If the pipeline cannot submit on a device timeline, use
`IWinMLPipeline::Run`.

`WINML_PIPELINE_EXECUTION_CAPABILITIES` values are
`WINML_PIPELINE_EXECUTION_CAPABILITY_NONE = 0`,
`WINML_PIPELINE_EXECUTION_CAPABILITY_REPLAYABLE = 0x1`, and
`WINML_PIPELINE_EXECUTION_CAPABILITY_ITERATIVE_REPLAY = 0x2`.

```
IID: 7de077a2-60ac-4748-babd-e09b0fd008d6
```

## `GetCapabilities`

```cpp
HRESULT GetCapabilities(
    [out, retval] WINML_PIPELINE_EXECUTION_CAPABILITIES* capabilities);
```

Returns capability flags for the built pipeline.

**Returns:** `S_OK` on success, `E_POINTER` for a null `capabilities`, or
`E_NOT_VALID_STATE` before pipeline initialization has completed.

---

## `Submit`

```cpp
HRESULT Submit(
    [in, unique] IWinMLFence* waitFence,
    [out, retval] IWinMLFence** completionFence);
```

Submits one asynchronous pipeline step. `waitFence`, when supplied, must complete
before this submission begins. `completionFence` receives a fence for the
submitted work.

**Returns:** `S_OK` on success, `E_POINTER` for a null `completionFence`,
`HRESULT_FROM_WIN32(ERROR_BUSY)` if execution is already active, or
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` when the built pipeline does not
support device-timeline submission.

### Example

```cpp
wil::com_ptr<IWinMLPipelineExecution> execution;
THROW_IF_FAILED(pipeline->QueryInterface(IID_PPV_ARGS(execution.put())));

wil::com_ptr<IWinMLFence> fence;
THROW_IF_FAILED(execution->Submit(nullptr, fence.put()));
```

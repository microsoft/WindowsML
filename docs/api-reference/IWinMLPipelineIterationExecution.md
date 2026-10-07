<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLPipelineIterationExecution

> Part of the [WinML Runtime API Reference](README.md).

Backend-specific extension for submitting a fixed number of replayed iterations
on a built pipeline. Query it from `IWinMLPipeline` after `Build`.

```
IID: 11a68236-b74b-47f7-a7c9-faf551792686
```

## `SubmitIterations`

```cpp
HRESULT SubmitIterations(
    [in] UINT32 iterationCount,
    [in, unique] IWinMLFence* waitFence,
    [out, retval] IWinMLFence** completionFence);
```

Submits `iterationCount` iterations. `waitFence`, when supplied, is used before
the first iteration. `completionFence` receives the fence for the final
iteration.

**Returns:** `S_OK` on success, `E_POINTER` for a null `completionFence`,
`E_INVALIDARG` when `iterationCount` is zero,
`HRESULT_FROM_WIN32(ERROR_BUSY)` if execution is already active, or
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` when the pipeline has no
next-iteration connection or cannot submit asynchronously.

### Example

```cpp
wil::com_ptr<IWinMLPipelineIterationExecution> iterationExecution;
THROW_IF_FAILED(pipeline->QueryInterface(IID_PPV_ARGS(iterationExecution.put())));

wil::com_ptr<IWinMLFence> fence;
THROW_IF_FAILED(iterationExecution->SubmitIterations(4, nullptr, fence.put()));
```

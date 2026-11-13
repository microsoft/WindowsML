<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLStageHostBuffers

> Part of the [WinML Runtime API Reference](README.md).

Binds caller-owned host memory to stage inputs and outputs in one call, without
creating tensor objects. Query any [`IWinMLStage`](IWinMLStage.md) for this
interface.

```
IID: a53a28b2-b597-4773-9c5f-aadfb1945ad6
```

## `BindHostBuffers`

```cpp
HRESULT BindHostBuffers(
    [in] UINT32 inputCount,
    [in, size_is(inputCount)] const WINML_HOST_BUFFER_BINDING* inputs,
    [in] UINT32 outputCount,
    [in, size_is(outputCount)] const WINML_HOST_BUFFER_BINDING* outputs,
    [in, unique] IUnknown* lifetimeKeepAlive);
```

Binds every listed input and output. Execution reads each input from, and writes
each output into, the caller's memory; a stage that runs on a device moves the
data between that memory and the device as it does for any host tensor. An input
binding follows the rules of `BindInput` and an output binding the rules of
`BindOutput`, including the output descriptor check.

Each [`WINML_HOST_BUFFER_BINDING`](Structures.md#winml_host_buffer_binding)
names the input or output position, its tensor descriptor, the buffer and its
size, which must equal the size the descriptor implies, and the access mode
(`WINML_TENSOR_ACCESS_MODE_READ` or `READWRITE`; outputs require `READWRITE`).
The buffer is aligned for the element type. The descriptor's shape is copied
during the call.

| Behavior | Detail |
|---|---|
| Lifetime | The caller keeps each buffer valid until its position is rebound or reset, or the stage is released. When `lifetimeKeepAlive` is not null, the runtime references it until none of the buffers bound by this call remain bound, so its final `Release`, which can occur on any thread, can free them. |
| Rebinding | Binding a position again with the same memory, descriptor, and access mode keeps the binding and treats the contents as changed, so the next execution reads the current contents of a `READ` input. |
| Atomicity | Every entry is checked before any binding changes, so a failed call changes no binding. |
| Threading | Callable from any thread. Returns `HRESULT_FROM_WIN32(ERROR_BUSY)` while the stage's pipeline builds or executes. |

Python exposes `Stage.bind_input_buffer(index, buffer, *, access, target)`,
`Stage.bind_output_buffer(index, buffer, *, target)`, and
`Stage.bind_buffers(inputs, outputs, *, access)` for objects that support the
buffer protocol, such as NumPy arrays.

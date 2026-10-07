<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLStage

> Part of the [WinML Runtime API Reference](README.md).

Stage handle returned by `IWinMLPipelineBuilder::AddModelStage` or
`AddProcessorStage`. Use stage handles to bind inputs, publish outputs, and query
the target selected for the built pipeline.

Input bindings are positional and persist until replaced. `ResetExecutionState`
does not clear them.

```
IID: 88596a2b-24fe-476e-bc90-c37161edb477
```

## `GetDebugName`

```cpp
HRESULT GetDebugName([out, string] LPCWSTR* debugName);
```

Returns the diagnostic name stored on the stage.

**Returns:** `S_OK` on success or `E_POINTER` for a null `debugName`.

---

## `BindInput`

```cpp
HRESULT BindInput([in] UINT32 index, [in] IWinMLTensor* tensor);
```

Binds `tensor` to input slot `index`. Passing `nullptr` clears the input binding.

**Returns:** `S_OK` on success, `E_INVALIDARG` for an out-of-range `index`, or
`HRESULT_FROM_WIN32(ERROR_BUSY)` when the owning pipeline is executing or another
binding mutation owns the pipeline lock.

---

## `BindOutput`

```cpp
HRESULT BindOutput([in] UINT32 index, [in] IWinMLTensor* tensor);
```

Publishes output slot `index` into caller-owned storage. Sparse output bindings
are supported.

For recorded device-backed processor outputs that are not live inside the graph,
bind the output before Build so the runtime can establish fixed execution
storage. After Build, caller CPU destinations may be replaced, but recorded
device storage cannot.

**Returns:** `S_OK` on success, `E_POINTER` for a null `tensor`, `E_INVALIDARG`
for an out-of-range `index`, `HRESULT_FROM_WIN32(ERROR_BUSY)` during execution or
binding mutation, or `E_NOT_VALID_STATE` when changing that output is no longer
valid for the built pipeline.

---

## `RequestOutput`

```cpp
HRESULT RequestOutput([in] UINT32 index);
```

Publishes output slot `index` into runtime-owned storage. Retrieve the result
with `GetOutput` after successful execution. `Run` produces a host-materialized
tensor; `Submit` produces a device-resident tensor with producer synchronization.

**Returns:** `S_OK` on success, `E_INVALIDARG` for an out-of-range `index`,
`HRESULT_FROM_WIN32(ERROR_BUSY)` during execution or binding mutation,
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` when the built graph cannot return a
runtime-owned output for that stage, or `E_NOT_VALID_STATE` when changing that
output is no longer valid for the built pipeline.

---

## `ResetOutput`

```cpp
HRESULT ResetOutput([in] UINT32 index);
```

Removes caller-visible publication for output slot `index`. Connected consumers
can still use that output inside the graph.

**Returns:** `S_OK` on success, `E_INVALIDARG` for an out-of-range `index`,
`HRESULT_FROM_WIN32(ERROR_BUSY)` during execution or binding mutation, or
`E_NOT_VALID_STATE` when changing that output is no longer valid for the built
pipeline.

---

## `GetOutput`

```cpp
HRESULT GetOutput([in] UINT32 index, [out, retval] IWinMLTensor** tensor);
```

Returns the tensor published for output slot `index`. For caller-bound outputs,
this can be called before execution. For runtime-owned outputs, call it after a
successful `Run` or `Submit` has produced the output. Unpublished outputs return
`HRESULT_FROM_WIN32(ERROR_NOT_FOUND)`.

**Returns:** `S_OK` on success, `E_POINTER` for a null `tensor`, `E_INVALIDARG`
for an out-of-range `index`, `HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` when the
output is not published, or `E_NOT_VALID_STATE` when a runtime-owned output has
not produced a tensor yet.

---

## `GetExecutionTarget`

```cpp
HRESULT GetExecutionTarget(
    [out, retval] IWinMLExecutionTarget** target);
```

Returns the stage execution target after pipeline build.

**Returns:** `S_OK` on success, `E_POINTER` for a null `target`,
`E_NOT_VALID_STATE` before the stage is attached to a built pipeline, or
`HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` if no target is available.

### Example

```cpp
THROW_IF_FAILED(stage->BindInput(0, inputTensor.get()));
THROW_IF_FAILED(stage->RequestOutput(0));
THROW_IF_FAILED(pipeline->Run());

wil::com_ptr<IWinMLTensor> outputTensor;
THROW_IF_FAILED(stage->GetOutput(0, outputTensor.put()));
```

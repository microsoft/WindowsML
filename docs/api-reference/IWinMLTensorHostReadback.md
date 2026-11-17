<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLTensorHostReadback

> Part of the [WinML Runtime API Reference](README.md).

**Optional host readback for a tensor backed by a D3D12 buffer.**

Obtain this interface with `QueryInterface` from [`IWinMLTensor`](IWinMLTensor.md).
Tensors that aren't backed by a D3D12 buffer don't expose it.

```
IID: 63bc927b-9368-4095-b000-5a094fc23151
```

With host readback enabled, a pipeline execution that writes the tensor as a
caller-bound output can also copy it into a persistently mapped, host-visible
buffer as part of the same device submission. A later
`Lock(WINML_TENSOR_LOCK_MODE_READ, ...)` returns a view of that buffer. It waits
for the producing execution to complete if necessary and issues no additional
device work.

Use it for small device outputs that the host reads after every execution, such
as sampled token IDs, so that the read costs a fence check and a pointer instead
of a synchronous device-to-host copy.

Host readback is a fast path, not a guarantee. Whether a valid host copy exists
can depend on work the caller doesn't see (see
[Validity of the host copy](#validity-of-the-host-copy)), so always pass
`WINML_TENSOR_LOCK_FLAG_ALLOW_SYNCHRONIZED_CPU_ACCESS`. `Lock` then falls back to
the synchronized readback instead of failing.

## `SetHostReadbackEnabled`

```cpp
HRESULT SetHostReadbackEnabled([in] BOOL enabled);
```

Enables or disables host readback.

| Parameter | Description |
|---|---|
| `enabled` | `TRUE` to capture the tensor on the host when an execution writes it, `FALSE` to stop. |

**Remarks:** Enabling allocates the host-visible buffer once, sized to the
tensor's buffer binding, and every later execution reuses it. Enable it before
the first execution that writes the tensor. Changing it later makes that
pipeline record its device work again once. Don't call it concurrently with an
execution that uses the tensor.

**Returns:** `S_OK` on success,
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` for a tensor without a D3D12 buffer
binding, or `HRESULT_FROM_WIN32(ERROR_LOCKED)` while the tensor is locked.

---

## `GetHostReadbackEnabled`

```cpp
HRESULT GetHostReadbackEnabled([out, retval] BOOL* enabled);
```

Returns whether host readback is enabled.

| Parameter | Description |
|---|---|
| `enabled` | Receives `TRUE` when host readback is enabled, otherwise `FALSE`. |

---

## Validity of the host copy

A host copy is valid until the tensor is next written, which happens with any
of these.

- Another pipeline execution that binds the tensor as an output or as
  caller-owned state.
- A write lock or a tensor operation such as `IWinMLMutableTensor::CopyFrom`.
- An execution that fails after it starts.
- A ready fence that the caller attaches through
  [`IWinMLTensorSynchronization::SetReadyFence`](IWinMLTensorSynchronization.md).

When no valid host copy exists, for example because the backend that wrote the
tensor doesn't capture host copies, `Lock` falls back to the standard
synchronized readback, which requires
`WINML_TENSOR_LOCK_FLAG_ALLOW_SYNCHRONIZED_CPU_ACCESS`.

Windows ML can't observe device work that the caller records against the
underlying resource directly. After such work, attach its ready fence with
`SetReadyFence` before locking the tensor. The host copy is tracked per tensor
object, so a write through another tensor object over the same D3D12 resource,
such as one created with `CreateTensorFromBuffer`, also needs its ready fence
attached to this tensor.

A read lock served from the host copy aliases the host-visible buffer. Release
it before submitting the next execution that writes the tensor.

## Example

```cpp
// Once, before the first execution.
wil::com_ptr<IWinMLTensorHostReadback> hostReadback;
THROW_IF_FAILED(tokenIds->QueryInterface(IID_PPV_ARGS(hostReadback.put())));
THROW_IF_FAILED(hostReadback->SetHostReadbackEnabled(TRUE));
THROW_IF_FAILED(stage->BindOutput(0, tokenIds.get()));

// Every step.
wil::com_ptr<IWinMLFence> completion;
THROW_IF_FAILED(execution->Submit(nullptr, completion.put()));
THROW_IF_FAILED(completion->Wait(INFINITE));
wil::com_ptr<IWinMLTensorDataLock> lock;
THROW_IF_FAILED(tokenIds->Lock(WINML_TENSOR_LOCK_MODE_READ,
                               WINML_TENSOR_LOCK_FLAG_ALLOW_SYNCHRONIZED_CPU_ACCESS,
                               lock.put()));
BYTE* data = nullptr;
UINT64 size = 0;
THROW_IF_FAILED(lock->GetData(&data, &size));
```

In Python, `Tensor.set_host_readback_enabled(enabled)` and
`Tensor.host_readback_enabled()` wrap the two methods.

<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLTensorSynchronization

> Part of the [WinML Runtime API Reference](README.md).

**Ready-fence metadata for a tensor.**

Obtain this interface with `QueryInterface` from [`IWinMLTensor`](IWinMLTensor.md).

```
IID: f457998f-22d4-48bf-aba0-a75ae59ab666
```

## `GetReadyFence`

```cpp
HRESULT GetReadyFence([out, retval] IWinMLFence** readyFence);
```

Returns the fence attached to the tensor.

| Parameter | Description |
|---|---|
| `readyFence` | Receives the ready fence, or `NULL` when no fence is attached. |

**Returns:** `E_POINTER` when `readyFence` is `NULL`. The returned [`IWinMLFence`](IWinMLFence.md) reference is owned by the caller.

---

## `SetReadyFence`

```cpp
HRESULT SetReadyFence([in, unique] IWinMLFence* readyFence);
```

Attaches or clears the tensor's ready fence.

| Parameter | Description |
|---|---|
| `readyFence` | Fence to attach. Pass `NULL` to clear the current fence. |

**Remarks:** `SetReadyFence` stores a COM reference to `readyFence`. Pass `NULL` to clear the fence. Call `SetReadyFence` as a single-threaded handoff before giving the tensor to the runtime; it is not safe to call concurrently with runtime use of the tensor. Synchronized CPU access to a device-backed tensor waits for the attached fence before reading the tensor data.

## Example

```cpp
wil::com_ptr<IWinMLTensor> tensor;
wil::com_ptr<IWinMLFence> readyFence;
if (!tensor) return S_OK;

wil::com_ptr<IWinMLTensorSynchronization> sync;
HRESULT hr = tensor->QueryInterface(IID_PPV_ARGS(sync.put()));
if (FAILED(hr)) return hr;

hr = sync->SetReadyFence(readyFence.get());
if (FAILED(hr)) return hr;

wil::com_ptr<IWinMLFence> currentFence;
return sync->GetReadyFence(currentFence.put());
```

---

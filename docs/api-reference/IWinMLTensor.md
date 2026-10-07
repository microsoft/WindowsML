<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLTensor

> Part of the [WinML Runtime API Reference](README.md).

**Typed multi-dimensional array for stage inputs and outputs.**

Create tensors through factories obtained from [`IWinMLExecutionTarget`](IWinMLExecutionTarget.md), such as [`IWinMLRawTensorFactory`](IWinMLRawTensorFactory.md). Optional tensor capabilities are discovered with `QueryInterface`.

```
IID: 22f5cf71-27be-4a1b-9087-1a36f7a230ad
```

## `GetDesc`

```cpp
HRESULT GetDesc([out, retval] WINML_TENSOR_DESC* desc);
```

Returns the tensor's concrete data type and shape.

| Parameter | Description |
|---|---|
| `desc` | Receives the descriptor. On output, `desc->dimensions` points to tensor-owned storage that remains valid for the lifetime of the tensor. |

---

## `Lock`

```cpp
HRESULT Lock(
    [in] WINML_TENSOR_LOCK_MODE mode,
    [in] WINML_TENSOR_LOCK_FLAGS flags,
    [out, retval] IWinMLTensorDataLock** dataLock
);
```

Locks the tensor for CPU access.

| Parameter | Description |
|---|---|
| `mode` | Access mode: `WINML_TENSOR_LOCK_MODE_READ`, `WINML_TENSOR_LOCK_MODE_WRITE`, or `WINML_TENSOR_LOCK_MODE_READ_WRITE`. |
| `flags` | `WINML_TENSOR_LOCK_FLAG_NONE` for direct CPU access only, or `WINML_TENSOR_LOCK_FLAG_ALLOW_SYNCHRONIZED_CPU_ACCESS` to allow staged readback or copy-back for tensors whose backing store is not directly CPU-accessible. |
| `dataLock` | Receives the lock object that owns the CPU-access window. |

**Returns:** `E_INVALIDARG` for an invalid mode or unknown flag. `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` is returned when direct CPU access is unavailable and synchronized access was not requested. A conflicting lock returns `HRESULT_FROM_WIN32(ERROR_LOCKED)`.

**Remarks:** CPU-backed tensors can be locked directly. Device-backed tensors may require `WINML_TENSOR_LOCK_FLAG_ALLOW_SYNCHRONIZED_CPU_ACCESS`; a synchronized read waits for the tensor's ready fence when one is attached, copies the requested bytes to CPU-visible storage, and returns after the copy completes. Pipeline-published outputs obtained after `IWinMLPipeline::Run` completes are CPU-readable with `Lock(WINML_TENSOR_LOCK_MODE_READ, WINML_TENSOR_LOCK_FLAG_NONE, ...)`. Caller-prebound CPU-resident outputs have the same direct-read contract; caller-prebound device-resident outputs may require synchronized access.

Multiple `READ` locks may coexist. `WRITE` and `READ_WRITE` locks are exclusive, including conflicts from the same thread. `WINML_TENSOR_LOCK_MODE_WRITE` does not preserve existing contents; use `WINML_TENSOR_LOCK_MODE_READ_WRITE` when the caller must read and then modify the tensor. Writable synchronized locks expose [`IWinMLTensorSynchronizedDataLock`](IWinMLTensorSynchronizedDataLock.md).

## Example

```cpp
wil::com_ptr<IWinMLTensor> tensor;
if (!tensor) return S_OK;

WINML_TENSOR_DESC desc{};
HRESULT hr = tensor->GetDesc(&desc);
if (FAILED(hr)) return hr;

wil::com_ptr<IWinMLTensorDataLock> lock;
hr = tensor->Lock(WINML_TENSOR_LOCK_MODE_READ, WINML_TENSOR_LOCK_FLAG_NONE, lock.put());
if (hr == HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED))
{
    hr = tensor->Lock(
        WINML_TENSOR_LOCK_MODE_READ,
        WINML_TENSOR_LOCK_FLAG_ALLOW_SYNCHRONIZED_CPU_ACCESS,
        lock.put());
}
if (FAILED(hr)) return hr;

BYTE* data = nullptr;
UINT64 byteCount = 0;
return lock->GetData(&data, &byteCount);
```

---

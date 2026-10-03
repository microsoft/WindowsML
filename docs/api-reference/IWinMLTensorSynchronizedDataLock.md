<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLTensorSynchronizedDataLock

> Part of the [WinML Runtime API Reference](README.md).

Extension of `IWinMLTensorDataLock` returned by writable synchronized locks.

```
IID: c1754906-62ce-46a2-a2fb-cc13cd10ffcd
```

## `Commit`

```cpp
HRESULT Commit();
```

Publishes staged writes back to the tensor. Releasing a writable synchronized lock without calling `Commit()` discards those staged writes.

**Returns:** `E_NOT_VALID_STATE` if the lock does not represent a writable
synchronized access path.

## Example

```cpp
wil::com_ptr<IWinMLTensor> tensor;
if (!tensor) return S_OK;

wil::com_ptr<IWinMLTensorDataLock> lock;
HRESULT hr = tensor->Lock(
    WINML_TENSOR_LOCK_MODE_WRITE,
    WINML_TENSOR_LOCK_FLAG_ALLOW_SYNCHRONIZED_CPU_ACCESS,
    lock.put());
if (FAILED(hr)) return hr;

BYTE* data = nullptr;
UINT64 byteCount = 0;
hr = lock->GetData(&data, &byteCount);
if (FAILED(hr)) return hr;

wil::com_ptr<IWinMLTensorSynchronizedDataLock> synchronizedLock;
hr = lock->QueryInterface(IID_PPV_ARGS(synchronizedLock.put()));
if (hr == E_NOINTERFACE) return S_OK;
if (FAILED(hr)) return hr;
return synchronizedLock->Commit();
```

---

<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLTensorDataLock

> Part of the [WinML Runtime API Reference](README.md).

Owns a CPU-access window obtained from `IWinMLTensor::Lock`.

```
IID: 7d2780ed-66b4-4fa5-a935-bae6d28082bd
```

## `GetData`

```cpp
HRESULT GetData(
    [out] BYTE** data,
    [out] UINT64* dataSizeInBytes
);
```

Returns a CPU-accessible pointer that remains valid for the lifetime of the
lock.

| Parameter | Description |
|---|---|
| `data` | Receives the CPU-accessible data pointer. |
| `dataSizeInBytes` | Receives the accessible byte size. |

**Returns:** `E_POINTER` when either output parameter is `NULL`.

## Example

```cpp
wil::com_ptr<IWinMLTensorDataLock> lock;
if (!lock) return S_OK;

BYTE* data = nullptr;
UINT64 byteCount = 0;
HRESULT hr = lock->GetData(&data, &byteCount);
if (FAILED(hr)) return hr;

if (data != nullptr && byteCount >= sizeof(float))
{
    float first = *reinterpret_cast<float*>(data);
    (void)first;
}
return S_OK;
```

---

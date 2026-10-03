<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLMutableTensor

> Part of the [WinML Runtime API Reference](README.md).

**Tensor data movement operations.**

Obtain this interface with `QueryInterface` from [`IWinMLTensor`](IWinMLTensor.md). The data movement path varies by tensor backing store.

```
IID: a7c3e1d4-6f82-4b59-9e1a-3d8c5f2a7b94
```

## `CopyFrom`

```cpp
HRESULT CopyFrom([in] IWinMLTensor* source);
```

Copies data from `source` into this tensor.

| Parameter | Description |
|---|---|
| `source` | Source tensor whose contents are copied into this tensor. |

**Returns:** `E_POINTER` when `source` is `NULL`. `E_INVALIDARG` is returned when the source and destination byte sizes differ.

---

## `Fill`

```cpp
HRESULT Fill(
    [in] const void* value,
    [in] UINT32 valueSizeInBytes
);
```

Sets every element in the tensor to the same value.

| Parameter | Description |
|---|---|
| `value` | Pointer to one element encoded in the tensor's data type. |
| `valueSizeInBytes` | Must exactly match the tensor element size. |

**Returns:** `E_POINTER` when `value` is `NULL`. `E_INVALIDARG` is returned when `valueSizeInBytes` is zero, does not match the tensor element size, or the tensor uses a packed sub-byte data type.

## Example

```cpp
wil::com_ptr<IWinMLTensor> tensor;
if (!tensor) return S_OK;

wil::com_ptr<IWinMLMutableTensor> mutableTensor;
HRESULT hr = tensor->QueryInterface(IID_PPV_ARGS(mutableTensor.put()));
if (FAILED(hr)) return hr;

float zero = 0.0f;
return mutableTensor->Fill(&zero, sizeof(zero));
```

---

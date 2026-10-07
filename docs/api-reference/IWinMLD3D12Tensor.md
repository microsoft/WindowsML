<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLD3D12Tensor

> Part of the [WinML Runtime API Reference](README.md).

**Optional D3D12 buffer interop capability for a tensor.**

Obtain this interface with `QueryInterface` from [`IWinMLTensor`](IWinMLTensor.md). CPU-only tensors return `E_NOINTERFACE`.

```
IID: 37ac4145-5c22-4aa4-bdb2-d70f050849fb
```

## `GetBufferBinding`

```cpp
HRESULT GetBufferBinding([out, retval] WINML_BUFFER_BINDING* binding);
```

Returns the tensor's D3D12 buffer binding.

| Parameter | Description |
|---|---|
| `binding` | Receives the [`WINML_BUFFER_BINDING`](Structures.md#winml_buffer_binding), including the underlying resource, byte offset, and byte size. |

**Returns:** `E_POINTER` when `binding` is `NULL`.

**Remarks:** `binding->resource` is returned with one reference owned by the caller. Release it when it is no longer needed.

## Example

```cpp
wil::com_ptr<IWinMLTensor> tensor;
if (!tensor) return S_OK;

wil::com_ptr<IWinMLD3D12Tensor> d3d12Tensor;
HRESULT hr = tensor->QueryInterface(IID_PPV_ARGS(d3d12Tensor.put()));
if (hr == E_NOINTERFACE) return S_OK;
if (FAILED(hr)) return hr;

WINML_BUFFER_BINDING binding{};
hr = d3d12Tensor->GetBufferBinding(&binding);
if (SUCCEEDED(hr) && binding.resource != nullptr)
{
    binding.resource->Release();
}
return hr;
```

---

<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLRawTensorFactory

> Part of the [WinML Runtime API Reference](README.md).

**Target-bound factory for raw tensor allocation and raw buffer interop.**

Obtain this interface with `QueryInterface` from [`IWinMLExecutionTarget`](IWinMLExecutionTarget.md).

```
IID: b1c61714-e327-445e-b524-33e46e986fc0
```

## `CreateTensor`

```cpp
HRESULT CreateTensor(
    [in] const WINML_TENSOR_DESC* desc,
    [in, size_is(byteCount), unique] const void* data,
    [in] UINT64 byteCount,
    [out, retval] IWinMLTensor** tensor
);
```

Allocates a tensor described by `desc`, optionally initialized from caller-supplied bytes.

| Parameter | Description |
|---|---|
| `desc` | Concrete tensor descriptor. Allocated tensors must have fully resolved dimensions. |
| `data` | Optional initialization bytes. Pass `NULL` to allocate without supplying initial contents. |
| `byteCount` | Size of `data` in bytes. When `data` is non-NULL, this must exactly match the tensor's required byte size. |
| `tensor` | Receives the created tensor. |

**Remarks:** `desc` must contain concrete dimensions. If `data` is supplied, `byteCount` must match the tensor byte size.

---

## `CreateTensorFromBuffer`

```cpp
HRESULT CreateTensorFromBuffer(
    [in] const WINML_TENSOR_DESC* desc,
    [in] const WINML_BUFFER_BINDING* binding,
    [out, retval] IWinMLTensor** tensor
);
```

Creates a tensor that aliases an existing GPU buffer binding.

| Parameter | Description |
|---|---|
| `desc` | Tensor descriptor that describes the bound bytes. |
| `binding` | Existing [`WINML_BUFFER_BINDING`](Structures.md#winml_buffer_binding) to wrap. |
| `tensor` | Receives the created tensor. |

**Remarks:** `binding->resource` must be queryable for `ID3D12Resource`, must be a buffer created with `D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS`, and must belong to the D3D12 device associated with this factory's execution target. The binding range must contain the byte size implied by `desc`.

---

## `CreateTensorFromRawBuffer`

```cpp
HRESULT CreateTensorFromRawBuffer(
    [in] const WINML_TENSOR_DESC* desc,
    [in, size_is(byteCount)] BYTE* data,
    [in] UINT64 byteCount,
    [in] WINML_TENSOR_ACCESS_MODE accessMode,
    [in, unique] IUnknown* lifetimeKeepAlive,
    [out, retval] IWinMLTensor** tensor
);
```

Creates a tensor over a caller-owned CPU buffer that already matches the requested tensor layout and data type exactly.

| Parameter | Description |
|---|---|
| `desc` | Tensor descriptor for the caller-owned bytes. |
| `data` | Caller-owned buffer to wrap or copy. |
| `byteCount` | Size of `data` in bytes. This must exactly match the tensor's required byte size. |
| `accessMode` | Declares whether the runtime copies the buffer, reads it in place, or reads and writes it in place. |
| `lifetimeKeepAlive` | Optional object AddRef'd for the tensor lifetime to keep the backing storage alive. |
| `tensor` | Receives the created tensor. |

**Remarks:**

| Access mode | Behavior |
|---|---|
| `WINML_TENSOR_ACCESS_MODE_COPY` | The runtime takes a private copy. The caller may free or reuse `data` after the call returns. |
| `WINML_TENSOR_ACCESS_MODE_READ` | The tensor is a read-only view of `data`; write locks fail. |
| `WINML_TENSOR_ACCESS_MODE_READWRITE` | The tensor is a read-write view of `data`; tensor writes update the caller's buffer. |

The caller must keep `data` valid for the tensor lifetime for `READ` and `READWRITE`. `lifetimeKeepAlive`, when supplied, is AddRef'd and held by the tensor.

---

## `CreateTensorFromRegion`

```cpp
HRESULT CreateTensorFromRegion(
    [in] IWinMLTensor* source,
    [in, size_is(axisCount)] const UINT32* starts,
    [in, size_is(axisCount)] const UINT32* extents,
    [in] UINT32 axisCount,
    [out, retval] IWinMLTensor** tensor
);
```

Copies a multi-axis region from an existing tensor into a newly created tensor.

| Parameter | Description |
|---|---|
| `source` | Source tensor to copy from. |
| `starts` | Per-axis start coordinates in `source`. |
| `extents` | Per-axis extents for the copied region. The output shape matches these extents. |
| `axisCount` | Number of axes described by `starts` and `extents`. This must match the rank of `source`. |
| `tensor` | Receives the copied region as a new tensor. |

**Returns:** `E_INVALIDARG` is returned when `axisCount` does not match the source rank or the requested region is outside the source tensor. The returned tensor owns independent storage.

## Example

```cpp
wil::com_ptr<IWinMLExecutionTarget> target;
if (!target) return S_OK;

wil::com_ptr<IWinMLRawTensorFactory> factory;
HRESULT hr = target->QueryInterface(IID_PPV_ARGS(factory.put()));
if (FAILED(hr)) return hr;

const UINT64 dims[] = {1, 4};
const float values[] = {1.0f, 2.0f, 3.0f, 4.0f};
WINML_TENSOR_DESC desc{};
desc.dataType = WINML_TENSOR_DATA_TYPE_FLOAT32;
desc.dimensionCount = ARRAYSIZE(dims);
desc.dimensions = dims;

wil::com_ptr<IWinMLTensor> tensor;
return factory->CreateTensor(&desc, values, sizeof(values), tensor.put());
```

---

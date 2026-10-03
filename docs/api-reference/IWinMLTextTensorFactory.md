<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLTextTensorFactory

> Part of the [WinML Runtime API Reference](README.md).

**Target-bound factory for token tensor packing and extraction.**

Obtain this interface with `QueryInterface` from [`IWinMLExecutionTarget`](IWinMLExecutionTarget.md).

`WINML_TOKEN_TENSOR_DESC` describes the token tensor format through `dataType` and `batchSize`. See [Structures.md#winml_token_tensor_desc](Structures.md#winml_token_tensor_desc).

```
IID: 4c69327b-62ed-40f7-84d4-dfdf8df2a3ba
```

## `CreateTokenTensor`

```cpp
HRESULT CreateTokenTensor(
    [in, size_is(tokenCount)] const UINT32* tokenIds,
    [in] UINT32 tokenCount,
    [in] const WINML_TOKEN_TENSOR_DESC* desc,
    [out, retval] IWinMLTensor** tensor
);
```

Packs token IDs into a tensor.

| Parameter | Description |
|---|---|
| `tokenIds` | Source token IDs. |
| `tokenCount` | Number of token IDs in `tokenIds`. |
| `desc` | Token tensor descriptor. |
| `tensor` | Receives the created tensor. |

**Remarks:** The adapter packs a single token sequence into a rank-2 tensor. `desc->batchSize` must be `1`. `INT32` and `INT64` token tensors are supported. When `desc->dataType` is `WINML_TENSOR_DATA_TYPE_UNDEFINED`, the runtime defaults to `INT64`.

---

## `ReadTokenIdsFromTensor`

```cpp
HRESULT ReadTokenIdsFromTensor(
    [in] IWinMLTensor* tensor,
    [out] UINT32* tokenCount,
    [out, size_is(, *tokenCount)] UINT32** tokenIds
);
```

Extracts token IDs from a tensor.

| Parameter | Description |
|---|---|
| `tensor` | Source tensor whose token IDs are read back. |
| `tokenCount` | Receives the number of returned token IDs. |
| `tokenIds` | Receives a `CoTaskMemAlloc`-allocated `UINT32[]`. The caller frees it with `CoTaskMemFree`. |

**Remarks:** `INT32` and `INT64` token tensors are supported. The returned token count is taken from the tensor's last dimension.

This method allocates the returned array and may synchronize a device-backed tensor to CPU-visible storage.

## Example

```cpp
wil::com_ptr<IWinMLTextTensorFactory> factory;
if (!factory) return S_OK;

const UINT32 tokenIds[] = {101, 2023, 2003, 102};
WINML_TOKEN_TENSOR_DESC desc{};
desc.dataType = WINML_TENSOR_DATA_TYPE_INT64;
desc.batchSize = 1;

wil::com_ptr<IWinMLTensor> tensor;
return factory->CreateTokenTensor(tokenIds, ARRAYSIZE(tokenIds), &desc, tensor.put());
```

---

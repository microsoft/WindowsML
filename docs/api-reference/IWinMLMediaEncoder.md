<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLMediaEncoder

> Part of the [WinML Runtime API Reference](README.md).

**Encodes a media tensor into embedding rows for a segmented sequence stage.**

Obtain this interface from [`IWinMLSegmentedSequenceStage::CreateMediaEncoderFromFile`](IWinMLSegmentedSequenceStage.md) or `CreateMediaEncoderFromPipeline`. The returned layout and embedding tensor are used together with `IWinMLSegmentedSequenceStage::BindEmbeddingSegment`.

```
IID: 8b1e0a47-3d92-4f6c-b5a8-1c9f2e6d4073
```

## `GetMediaTensorDesc`

```cpp
HRESULT GetMediaTensorDesc([out, retval] WINML_TENSOR_DESC* desc);
```

Returns the media tensor shape and element type this encoder accepts.

| Parameter | Description |
|---|---|
| `desc` | Receives the accepted descriptor. The `dimensions` pointer remains valid until the encoder is released. |

**Returns:** `E_POINTER` when `desc` is `NULL`.

**Remarks:** A zero or `UINT64_MAX` extent in the descriptor accepts any nonzero extent on that axis.

---

## `GetEmbeddingLength`

```cpp
HRESULT GetEmbeddingLength([out, retval] UINT32* embeddingLength);
```

Returns the row length of every embedding this encoder produces.

| Parameter | Description |
|---|---|
| `embeddingLength` | Receives the embedding row length. |

**Returns:** `E_POINTER` when `embeddingLength` is `NULL`.

---

## `GetEmbeddingDataType`

```cpp
HRESULT GetEmbeddingDataType([out, retval] WINML_TENSOR_DATA_TYPE* dataType);
```

Returns the element type of the embedding rows this encoder produces.

**Returns:** `E_POINTER` when `dataType` is `NULL`.

---

## `Encode`

```cpp
HRESULT Encode(
    [in] IWinMLTensor* media,
    [in, out] IWinMLTensor** embeddings,
    [out] IWinMLSequenceSegmentLayout** layout);
```

Encodes one media tensor into embedding rows plus the layout that describes the
resulting run.

| Parameter | Description |
|---|---|
| `media` | Media tensor matching `GetMediaTensorDesc`. Borrowed for the duration of the call. |
| `embeddings` | In/out. A compatible prebound tensor is written and committed in place; otherwise the element is replaced with a tensor carrying one transferred reference. |
| `layout` | Receives the run's layout, transferred to the caller. |

**Returns:** `E_POINTER` when `media`, `embeddings`, or `layout` is `NULL`. `E_INVALIDARG` is returned when `media` does not match the accepted descriptor.

**Remarks:** If `*embeddings` is a compatible prebound tensor, `Encode` writes it in place and commits synchronized writes when needed. Otherwise `Encode` replaces `*embeddings` with a tensor carrying one transferred reference. The returned `layout` is also transferred to the caller and must outlive the `BindEmbeddingSegment` execution that consumes it. The layout retains no hidden embedding copy; pass the returned tensor and layout together to [`IWinMLSegmentedSequenceStage::BindEmbeddingSegment`](IWinMLSegmentedSequenceStage.md).

## Example

```cpp
wil::com_ptr<IWinMLMediaEncoder> encoder;
wil::com_ptr<IWinMLTensor> media;
if (!encoder || !media) return S_OK;

WINML_TENSOR_DESC accepted{};
HRESULT hr = encoder->GetMediaTensorDesc(&accepted);
if (FAILED(hr)) return hr;

wil::com_ptr<IWinMLTensor> embeddings;
wil::com_ptr<IWinMLSequenceSegmentLayout> layout;
return encoder->Encode(media.get(), embeddings.put(), layout.put());
```

---

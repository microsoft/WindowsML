<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLSequenceSegmentLayout

> Part of the [WinML Runtime API Reference](README.md).

Layout object returned by `IWinMLMediaEncoder::Encode` with an embedding tensor.
Pass the same tensor/layout pair to
[`IWinMLSegmentedSequenceStage::BindEmbeddingSegment`](IWinMLSegmentedSequenceStage.md#bindembeddingsegment).

```
IID: 2f5c4d18-7a63-4b9e-8c21-0d7e6a3f5b84
```

## `GetEmbeddingRowCount`

```cpp
HRESULT GetEmbeddingRowCount([out, retval] UINT32* rowCount);
```

Returns the number of rows in the embedding tensor. This is not a sequence
position count or a sequence-cell count.

**Returns:** `S_OK` on success or `E_POINTER` for a null `rowCount`.

---

## `GetPositionCount`

```cpp
HRESULT GetPositionCount([out, retval] UINT32* positionCount);
```

Returns the number of sequence positions consumed by the segment, including
producer-authored framing. Advance sequence-position accounting by this value,
not by the embedding row count.

**Returns:** `S_OK` on success or `E_POINTER` for a null `positionCount`.

---

## `GetSequenceCellCount`

```cpp
HRESULT GetSequenceCellCount([out, retval] UINT32* cellCount);
```

Returns the number of sequence cells required for the segment, including
producer-authored framing. Use this value for cell-capacity accounting.

**Returns:** `S_OK` on success or `E_POINTER` for a null `cellCount`.

### Example

```cpp
UINT32 positions = 0;
THROW_IF_FAILED(layout->GetPositionCount(&positions));
```

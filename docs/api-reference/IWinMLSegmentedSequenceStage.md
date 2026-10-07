<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLSegmentedSequenceStage

> Part of the [WinML Runtime API Reference](README.md).

Segmented-sequence extension for a built stage that can consume embedding
segments produced by `IWinMLMediaEncoder`. Query this interface from
`IWinMLStage`.

```
IID: 0a7f6b3c-91d4-4258-8e0b-5c2d3f7a9e16
```

## `GetEmbeddingLength`

```cpp
HRESULT GetEmbeddingLength([out, retval] UINT32* embeddingLength);
```

Returns the embedding row length after an encoder has established it.

**Returns:** `S_OK` on success, `E_POINTER` for a null `embeddingLength`,
`E_NOT_VALID_STATE` before Build or before embedding shape is known,
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` when media segments are not supported,
or `HRESULT_FROM_WIN32(ERROR_BUSY)` when binding or execution owns the pipeline.

---

## `GetEmbeddingDataType`

```cpp
HRESULT GetEmbeddingDataType([out, retval] WINML_TENSOR_DATA_TYPE* dataType);
```

Returns the embedding row element type after an encoder has established it.

**Returns:** `S_OK` on success, `E_POINTER` for a null `dataType`,
`E_NOT_VALID_STATE` before Build or before embedding shape is known,
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` when media segments are not supported,
or `HRESULT_FROM_WIN32(ERROR_BUSY)` when binding or execution owns the pipeline.

---

## `CreateMediaEncoderFromFile`

```cpp
HRESULT CreateMediaEncoderFromFile(
    [in, string] LPCWSTR encoderPath,
    [in, unique] IWinMLExecutionTarget* target,
    [out, retval] IWinMLMediaEncoder** encoder);
```

Creates an encoder for this stage from `encoderPath`. A null `target` uses the
stage's own residency. When `target` is non-null, it must be the stage target
selected by Build. The encoder holds a reference to the stage.

**Returns:** `S_OK` on success, `E_POINTER` for a null `encoder` or
`encoderPath`, `E_NOT_VALID_STATE` before Build,
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` for unsupported media or target
mismatch, `HRESULT_FROM_WIN32(ERROR_BUSY)` during execution or binding mutation,
or a load/build failure from the encoder path.

---

## `CreateMediaEncoderFromPipeline`

```cpp
HRESULT CreateMediaEncoderFromPipeline(
    IWinMLPipeline* pipeline,
    IWinMLStage* inputStage, UINT32 inputIndex,
    IWinMLStage* outputStage, UINT32 outputIndex,
    IWinMLMediaEncoder** encoder);
```

Wraps a built encoder pipeline. The pipeline and endpoint stages must belong to
that pipeline and the same runtime instance as the consumer stage. The endpoint
targets must match the consumer target. The input must be an unconnected per-run
input, the output must be reachable from it, and feedback or stateful encoder
pipelines are unsupported.

The encoder retains the pipeline and endpoints. Do not run, bind, reset, or share
that pipeline while the encoder is in use.

**Returns:** `S_OK` on success, `E_POINTER` for a null pointer argument,
`E_NOT_VALID_STATE` before Build, `E_INVALIDARG` for invalid endpoints, or
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` for unsupported media, feedback, stateful
encoder pipelines, or target mismatch.

---

## `BindEmbeddingSegment`

```cpp
HRESULT BindEmbeddingSegment(
    [in] UINT32 inputIndex,
    [in] IWinMLTensor* embeddings,
    [in] IWinMLSequenceSegmentLayout* layout);
```

Selects one embedding segment for the next execution in place of the token run at
`inputIndex`. The selection is cleared after that execution, and any bound tensor
at `inputIndex` is ignored for that execution.

`embeddings` and `layout` must be the pair produced by one
`IWinMLMediaEncoder::Encode` call from an encoder created by this same stage.
The objects are borrowed until execution completes or the selection is cleared.
The embedding run produces no decoder output; execute at least one token
afterward to obtain logits.

**Returns:** `S_OK` on success, `E_POINTER` for a null `embeddings` or `layout`,
`E_INVALIDARG` for an out-of-range index or tensor/layout mismatch,
`E_NOT_VALID_STATE` before Build, `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` when
the stage cannot consume the segment, or `HRESULT_FROM_WIN32(ERROR_BUSY)` during
execution or binding mutation.

---

## `ClearEmbeddingSegment`

```cpp
HRESULT ClearEmbeddingSegment([in] UINT32 inputIndex);
```

Clears a pending segment selection for `inputIndex`.

**Returns:** `S_OK` on success, `E_INVALIDARG` for an out-of-range input,
`E_NOT_VALID_STATE` before Build, `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` when
media segments are not supported, or `HRESULT_FROM_WIN32(ERROR_BUSY)` during
execution or binding mutation.

---

## `GetCapabilities`

```cpp
HRESULT GetCapabilities(
    [out, retval] WINML_SEGMENTED_SEQUENCE_CAPABILITIES* capabilities);
```

Reports segmented-sequence support for the built stage without constructing an
encoder or mutating bindings or execution state. Unsupported operations are
reported as `FALSE`. Shape fields remain unset until an encoder establishes the
embedding shape.

**Returns:** `S_OK` on success, `E_POINTER` for a null `capabilities`,
`E_NOT_VALID_STATE` before Build, or `HRESULT_FROM_WIN32(ERROR_BUSY)` during
execution or binding mutation.

### Example

```cpp
wil::com_ptr<IWinMLSegmentedSequenceStage> segmented;
THROW_IF_FAILED(stage->QueryInterface(IID_PPV_ARGS(segmented.put())));

WINML_SEGMENTED_SEQUENCE_CAPABILITIES capabilities{};
THROW_IF_FAILED(segmented->GetCapabilities(&capabilities));
```

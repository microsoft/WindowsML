<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLModelWeightFormats

> Part of the [WinML Runtime API Reference](README.md).

Describes how a loaded model stores its weights: the element encodings, bits per
element, block sizes, and sizes. Query an [`IWinMLModel`](IWinMLModel.md) for
`IWinMLModelWeightFormats`. Formats are computed on the first query and do not
change; for a model loaded from a file, that query reads the file's metadata
again. Methods may be called from any thread.

```
IWinMLModelWeightFormats  IID: 56066f5a-079a-4890-8b2f-24c0cddd549c
IWinMLModelWeightFormat   IID: f2bde5d5-80b2-4268-9e99-ba5fd1d87195
```

| Method | Returns |
|---|---|
| `GetWeightFormatCount` | The number of formats. Zero when the weights are not visible to the runtime, as in a precompiled execution provider context model, a shard of a split model other than the first, or a file that can no longer be read. |
| `GetWeightFormat(index, format)` | One `IWinMLModelWeightFormat`, in descending element count. `E_BOUNDS` for an index out of range. |
| `GetWeightDescription(hasDescription, description)` | A description for logging only, such as the format's own quantization label; a `CoTaskMemAlloc` string the caller frees. Do not parse it. |

Each `IWinMLModelWeightFormat` is an immutable snapshot:

| Method | Returns |
|---|---|
| `GetEncoding` | `WINML_WEIGHT_ENCODING`: `FLOAT` (including 8-bit and 4-bit float formats), `INTEGER` (scaled, optionally offset), `CODEBOOK` (indices into a table), or `UNKNOWN`. Treat an unrecognized value as `UNKNOWN`. |
| `GetBitsPerElement` | Bits that store one element, excluding scales. |
| `GetExponentBits` | For `FLOAT`, the exponent width that distinguishes formats of the same size (8 for bfloat16, 5 for float16); zero otherwise. |
| `GetBlockSize` | Elements that share one scale; zero when the format is not block-quantized or the size is not recorded. |
| `GetElementCount` | Elements stored in this format. |
| `GetByteCount` | Bytes that store them, including scales and offsets. |

| Model | Formats reported |
|---|---|
| ONNX | Initializers that the graph reads as tensor data, with the bit width, block size, and scales that a quantization operator records. |
| GGUF | Each tensor's storage type, with its block size. A split model reports all its files when loaded from the first file. |
| Compiled model | The element types and sizes of the constants the artifact stores. |

The C++ helpers `TryGetModelWeightFormats`, `GetModelWeightFormats`, and
`GetBitsPerWeight` in `winml/runtime/utils/WinMLRuntimeRAII.h` return the formats
as a vector and the average bits per weight. In Python, read
`Model.weight_formats`, `Model.bits_per_weight`, and `Model.weight_description`.

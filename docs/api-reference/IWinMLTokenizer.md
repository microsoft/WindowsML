<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLTokenizer

> Part of the [WinML Runtime API Reference](README.md).

Create the default tokenizer implementation with
`WinMLCreateTokenizerFromFile(_In_z_ LPCWSTR configFilePath, _COM_Outptr_ IWinMLTokenizer** tokenizer)`.
The path must be non-null and non-empty.

`Encode` and `Decode` do not retain per-stream decode state. Use
`CreateDecoder` when a caller needs incremental decoding for one generated
stream.

Additional tokenizer capabilities are exposed through `QueryInterface`:
[`IWinMLStructuredConversationFormatter`](IWinMLStructuredConversationFormatter.md)
is available when the tokenizer has a chat template;
[`IWinMLTokenizerConstraintFactory`](IWinMLTokenizerConstraintFactory.md) is
available when the tokenizer exposes the vocabulary needed for constraints;
[`IWinMLTokenizerMetadata`](IWinMLTokenizerMetadata.md) exposes BOS/EOS lookup.

```
IID: a7c1d5e3-9f48-4b62-8d1a-3e5c7f2b0a94
```

## `Encode`

```cpp
HRESULT Encode(
    [in, string] LPCWSTR text,
    [in] WINML_TOKENIZER_ENCODE_FLAGS flags,
    [out] UINT32* tokenCount,
    [out, size_is(, *tokenCount)] UINT32** tokenIds
);
```

Encodes text into token IDs.

| Parameter | Description |
|---|---|
| `text` | Text to tokenize. |
| `flags` | `WINML_TOKENIZER_ENCODE_FLAG_NONE` or `WINML_TOKENIZER_ENCODE_FLAG_ADD_SPECIAL_TOKENS`. |
| `tokenCount` | Receives the number of token IDs returned. |
| `tokenIds` | Receives a `CoTaskMemAlloc`-allocated token array. Caller frees it with `CoTaskMemFree`. |

**Returns:** `S_OK` on success, `E_POINTER` for null required pointers, or
`E_INVALIDARG` for flags outside the defined mask. On failure, `tokenIds` is
null and ownership is not transferred.

---

## `Decode`

```cpp
HRESULT Decode(
    [in, size_is(tokenCount)] const UINT32* tokenIds,
    [in] UINT32 tokenCount,
    [in] WINML_TOKENIZER_DECODE_FLAGS flags,
    [out, string] LPWSTR* text
);
```

Decodes token IDs into text.

| Parameter | Description |
|---|---|
| `tokenIds` | Token IDs to decode. |
| `tokenCount` | Number of token IDs in `tokenIds`. |
| `flags` | `WINML_TOKENIZER_DECODE_FLAG_NONE` or `WINML_TOKENIZER_DECODE_FLAG_SKIP_SPECIAL_TOKENS`. |
| `text` | Receives a `CoTaskMemAlloc`-allocated string. Caller frees it with `CoTaskMemFree`. |

**Returns:** `S_OK` on success, `E_POINTER` for null required pointers, or
`E_INVALIDARG` for flags outside the defined mask. On failure, `text` is null
and ownership is not transferred.

---

## `CreateDecoder`

```cpp
HRESULT CreateDecoder(
    [out, retval] IWinMLTokenizerDecoder** decoder
);
```

Creates an incremental decoder for one generated stream.

| Parameter | Description |
|---|---|
| `decoder` | Receives the new `IWinMLTokenizerDecoder*`. |

**Returns:** `S_OK` on success or `E_POINTER` when `decoder` is null.

---

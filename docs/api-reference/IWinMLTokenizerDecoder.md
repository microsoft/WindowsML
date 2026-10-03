<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLTokenizerDecoder

> Part of the [WinML Runtime API Reference](README.md).

Obtain this interface from `IWinMLTokenizer::CreateDecoder`. Each decoder owns
incremental decode state for one generated stream.

```
IID: d9479bde-aca5-4d03-83e2-03eee893bb7d
```

## `DecodeToken`

```cpp
HRESULT DecodeToken(
    [in] UINT32 tokenId,
    [out, string] LPCWSTR* text
);
```

Decodes one token using this decoder's current state.

| Parameter | Description |
|---|---|
| `tokenId` | Token ID to decode. |
| `text` | Receives a decoder-owned fragment pointer. Do not free it. The pointer is valid until the next call on this decoder. |

**Returns:** `S_OK` on success or `E_POINTER` when `text` is null.

---

## `Reset`

```cpp
HRESULT Reset();
```

Clears the decoder's streaming state.

**Returns:** `S_OK` on success.

---

<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLTokenizerMetadata

> Part of the [WinML Runtime API Reference](README.md).

Obtain this interface from `IWinMLTokenizer` with `QueryInterface`. BOS is a
single optional token ID. EOS can contain more than one token ID.

```
IID: c3987705-286b-4396-b0a0-49640385dd91
```

## `GetBosTokenId`

```cpp
HRESULT GetBosTokenId(
    [out] UINT32* tokenId
);
```

Returns the tokenizer's BOS token ID.

| Parameter | Description |
|---|---|
| `tokenId` | Receives the BOS token ID. |

**Returns:** `S_OK` when a BOS token is present, `S_FALSE` when none is defined,
or `E_POINTER` when `tokenId` is null.

---

## `GetEosTokenIds`

```cpp
HRESULT GetEosTokenIds(
    [out] UINT32* tokenCount,
    [out, size_is(, *tokenCount)] UINT32** tokenIds
);
```

Returns the tokenizer's EOS token IDs.

| Parameter | Description |
|---|---|
| `tokenCount` | Receives the number of EOS token IDs returned. |
| `tokenIds` | Receives a `CoTaskMemAlloc`-allocated token array. Caller frees it with `CoTaskMemFree`. |

**Returns:** `S_OK` when one or more EOS token IDs are present, `S_FALSE` when
none are defined, or `E_POINTER` for null output pointers.

---

## `FindTokenId`

```cpp
HRESULT FindTokenId(
    [in, string] LPCWSTR token,
    [out] UINT32* tokenId
);
```

Looks up the ID for one token string.

| Parameter | Description |
|---|---|
| `token` | Token string to resolve. |
| `tokenId` | Receives the resolved token ID. |

**Returns:** `S_OK` when the token is present, `S_FALSE` when it is not present,
or `E_POINTER` for null required pointers.

---

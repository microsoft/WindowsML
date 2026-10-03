<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLOrtModelSchema

> Part of the [WinML Runtime API Reference](README.md).
> See [IWinMLModelSchema](IWinMLModelSchema.md) for positional model-schema access.

ORT-specific name lookup extension for ORT-backed models. Query this interface
from `IWinMLModel` when names are needed for setup or diagnostics. Binding and
execution still use positional indices.

```
IID: 9451e992-e86b-411a-a4c0-f83d2a0e2a22
```

## `GetInputName`

```cpp
HRESULT GetInputName(
    [in] UINT32 index,
    [out, string] LPCWSTR* name
);
```

Returns the artifact input name at `index`.

**Returns:** `S_OK` on success, `E_POINTER` for a null `name`, `E_BOUNDS` for an
out-of-range `index`, or `HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` when metadata is
unavailable.

---

## `FindInputIndex`

```cpp
HRESULT FindInputIndex(
    [in, string] LPCWSTR name,
    [out, retval] UINT32* index
);
```

Finds the positional input index for `name`.

**Returns:** `S_OK` on success, `E_POINTER` for a null `index`, `E_INVALIDARG`
for a null `name`, or `HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` when metadata is
unavailable or the name is not found.

---

## `GetOutputName`

```cpp
HRESULT GetOutputName(
    [in] UINT32 index,
    [out, string] LPCWSTR* name
);
```

Returns the artifact output name at `index`.

**Returns:** `S_OK` on success, `E_POINTER` for a null `name`, `E_BOUNDS` for an
out-of-range `index`, or `HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` when metadata is
unavailable.

---

## `FindOutputIndex`

```cpp
HRESULT FindOutputIndex(
    [in, string] LPCWSTR name,
    [out, retval] UINT32* index
);
```

Finds the positional output index for `name`.

**Returns:** `S_OK` on success, `E_POINTER` for a null `index`, `E_INVALIDARG`
for a null `name`, or `HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` when metadata is
unavailable or the name is not found.

### Example

```cpp
wil::com_ptr<IWinMLOrtModelSchema> names;
THROW_IF_FAILED(model->QueryInterface(IID_PPV_ARGS(names.put())));

UINT32 inputIndex = 0;
THROW_IF_FAILED(names->FindInputIndex(L"input_ids", &inputIndex));
```

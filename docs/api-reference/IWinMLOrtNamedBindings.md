<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLOrtNamedBindings

> Part of the [WinML Runtime API Reference](README.md).
> See [IWinMLStage](IWinMLStage.md) for positional tensor binding.

ORT-specific setup convenience for ORT-backed stages. Query this interface from
`IWinMLStage`. The methods resolve an artifact name to a positional index, then
call the corresponding positional stage method.

Non-ORT stages return `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` from these
methods if the interface is reached through an implementation pointer; callers
normally discover availability with `QueryInterface`.

```
IID: dc39b5f1-ba7a-48f1-b6d3-a3eebbf4b352
```

## `BindInputByName`

```cpp
HRESULT BindInputByName(
    [in, string] LPCWSTR name,
    [in] IWinMLTensor* tensor
);
```

Resolves `name` to an input index and binds `tensor` there.

**Returns:** `S_OK` on success, `E_INVALIDARG` for a null name,
`HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` when the name is not found, or a failure
from `IWinMLStage::BindInput`.

---

## `BindOutputByName`

```cpp
HRESULT BindOutputByName(
    [in, string] LPCWSTR name,
    [in] IWinMLTensor* tensor
);
```

Resolves `name` to an output index and publishes caller-owned output storage.

**Returns:** `S_OK` on success, `E_INVALIDARG` for a null name,
`HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` when the name is not found, or a failure
from `IWinMLStage::BindOutput`.

---

## `GetOutputByName`

```cpp
HRESULT GetOutputByName(
    [in, string] LPCWSTR name,
    [out, retval] IWinMLTensor** tensor
);
```

Resolves `name` to an output index and returns that published output tensor.

**Returns:** `S_OK` on success, `E_POINTER` for a null `tensor`, `E_INVALIDARG`
for a null name, `HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` when the name is not
found, or a failure from `IWinMLStage::GetOutput`.

### Example

```cpp
wil::com_ptr<IWinMLOrtNamedBindings> namedBindings;
THROW_IF_FAILED(stage->QueryInterface(IID_PPV_ARGS(namedBindings.put())));
THROW_IF_FAILED(namedBindings->BindInputByName(L"input_ids", inputTensor.get()));
```

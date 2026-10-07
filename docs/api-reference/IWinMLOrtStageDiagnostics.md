<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLOrtStageDiagnostics

> Part of the [WinML Runtime API Reference](README.md).

ORT-specific diagnostics for an ORT-backed stage after pipeline build. Query this
interface from `IWinMLStage`.

```
IID: 9a146b25-52ae-4e21-b62b-005e5798e8eb
```

## `GetRequestedProviderName`

```cpp
HRESULT GetRequestedProviderName(
    [out, string] LPCWSTR* providerName
);
```

Returns the requested execution-provider name, or `NULL` when the stage did not
request one and left the choice to the runtime.

**Returns:** `S_OK` when a name is returned, `S_FALSE` when no name is available,
`E_POINTER` for a null `providerName`, `E_NOT_VALID_STATE` before Build, or
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` for a non-ORT stage.

---

## `GetSelectedProviderName`

```cpp
HRESULT GetSelectedProviderName(
    [out, string] LPCWSTR* providerName
);
```

Returns a representative execution-provider name recorded after session creation,
or `NULL` when no name is available. Automatic selection can use multiple
provider devices; this method does not report the complete selected set or
per-node graph assignment.

**Returns:** `S_OK` when a name is returned, `S_FALSE` when no name is available,
`E_POINTER` for a null `providerName`, `E_NOT_VALID_STATE` before Build, or
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` for a non-ORT stage.

---

## `IsProviderPinned`

```cpp
HRESULT IsProviderPinned([out, retval] BOOL* isPinned);
```

Reports whether the requested provider is pinned, meaning the runtime must use it
rather than treating it as a preference.

**Returns:** `S_OK` on success, `E_POINTER` for a null `isPinned`,
`E_NOT_VALID_STATE` before Build, or `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`
for a non-ORT stage.

### Example

```cpp
wil::com_ptr<IWinMLOrtStageDiagnostics> diagnostics;
THROW_IF_FAILED(stage->QueryInterface(IID_PPV_ARGS(diagnostics.put())));

BOOL pinned = FALSE;
THROW_IF_FAILED(diagnostics->IsProviderPinned(&pinned));
```

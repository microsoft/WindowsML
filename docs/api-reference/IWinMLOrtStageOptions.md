<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLOrtStageOptions

> Part of the [WinML Runtime API Reference](README.md).

ORT-specific construction options for an ORT-backed model stage. Set these
options before `IWinMLPipelineBuilder::Build`; setters return `E_NOT_VALID_STATE`
after Build consumes the stage.

```
IID: dd8497cb-a415-49cd-89bc-75aa61516e8a
```

Repeated provider-option and session-config keys replace the previous value for
that key. Runtime copies string arguments.

## `SetProviderOption`

```cpp
HRESULT SetProviderOption(
    [in, string] LPCWSTR key,
    [in, string] LPCWSTR value
);
```

Sets an execution-provider option for a provider-pinned stage. The value is
copied and used when the ORT session is created. Provider options require a
stage with a provider pin, supplied either by the execution target or the
artifact.

**Returns:** `S_OK` on success, `E_INVALIDARG` for a null `key`, null `value`, or
empty `key`, or `E_NOT_VALID_STATE` after Build. If provider options are present
without a provider pin, `Build` returns `E_INVALIDARG`.

---

## `SetSessionConfigEntry`

```cpp
HRESULT SetSessionConfigEntry(
    [in, string] LPCWSTR key,
    [in, string] LPCWSTR value
);
```

Sets an ORT session configuration entry used when the session is created,
including provider-specific entries used during automatic provider selection.

**Returns:** `S_OK` on success, `E_INVALIDARG` for a null `key`, null `value`, or
empty `key`, or `E_NOT_VALID_STATE` after Build.

---

## `SetSharedContextGroup`

```cpp
HRESULT SetSharedContextGroup(
    [in, string] LPCWSTR groupName
);
```

Assigns the stage to a named ORT shared-context group for the next Build. Runtime
copies the group name. All stages in a group must belong to the same pipeline and
use compatible provider and target settings.

**Returns:** `S_OK` on success, `E_INVALIDARG` for a null or empty `groupName`,
or `E_NOT_VALID_STATE` after Build. Build validates group membership and can
return `E_INVALIDARG` for invalid groups.

---

## `ClearSharedContextGroup`

```cpp
HRESULT ClearSharedContextGroup();
```

Removes the stage from its shared-context group.

**Returns:** `S_OK` on success or `E_NOT_VALID_STATE` after Build.

### Example

```cpp
wil::com_ptr<IWinMLOrtStageOptions> options;
THROW_IF_FAILED(stage->QueryInterface(IID_PPV_ARGS(options.put())));

THROW_IF_FAILED(options->SetProviderOption(
    L"<provider-option-key>",
    L"<provider-option-value>"));
THROW_IF_FAILED(options->SetSessionConfigEntry(L"custom.session.key", L"value"));
THROW_IF_FAILED(options->SetSharedContextGroup(L"shared-group"));
```

## C++ RAII facade

`WinMLRuntimeRAII.h` exposes the same operations on `WinML::Stage`:

```cpp
stage.SetOrtProviderOption(
    L"<provider-option-key>",
    L"<provider-option-value>");
stage.SetOrtSessionConfigEntry(L"custom.session.key", L"value");
stage.SetOrtSharedContextGroup(L"shared-group");
stage.ClearOrtSharedContextGroup();
```

## Python projection

Call `Stage.ort_options()` before building the pipeline:

```python
options = stage.ort_options()
options.set_provider_option("<provider-option-key>", "<provider-option-value>")
options.set_session_config("custom.session.key", "value")
options.set_shared_context_group("shared-group")
options.clear_shared_context_group()
```

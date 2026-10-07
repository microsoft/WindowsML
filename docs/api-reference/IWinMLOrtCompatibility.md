<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLOrtCompatibility

> Part of the [WinML Runtime API Reference](README.md).

**Compatibility extension for creating provider-pinned execution targets.**

Query this interface from [`IWinMLRuntime`](IWinMLRuntime.md). It creates an [`IWinMLExecutionTarget`](IWinMLExecutionTarget.md) that carries an execution-provider name and can be paired with a hardware target.

```
IID: a7a04319-3b8a-496c-af93-96de30311f82
```

## `CreateExecutionTarget`

```cpp
HRESULT CreateExecutionTarget(
    [in, string] LPCWSTR registeredProviderName,
    [in] WINML_EXECUTION_TARGET_KIND kind,
    [in, unique] IWinMLExecutionTarget* hardwareTarget,
    [out, retval] IWinMLExecutionTarget** target
);
```

Creates a provider-pinned execution target.

| Parameter | Description |
|---|---|
| `registeredProviderName` | Registered execution-provider name to use. |
| `kind` | Hardware class the provider-backed target represents. |
| `hardwareTarget` | Hardware target to pair with the provider, or `nullptr`. |
| `target` | Receives the created execution target. |

**Returns:** `S_OK` on success. Returns `E_POINTER` when `target` is null. Provider-name and hardware-target validation failures are returned from target initialization.

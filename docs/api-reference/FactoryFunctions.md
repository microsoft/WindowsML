<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Factory Functions

> Part of the [WinML Runtime API Reference](README.md).

These exported functions create Runtime, Task, and Tokenizer entry objects. The
Server is activated through header helpers instead; see
[Server activation](#server-activation).

## WinMLCreateRuntime

```cpp
STDAPI WinMLCreateRuntime(
    REFIID riid,
    void** runtime
);
```

Creates the process-local WinML Runtime instance.

| Parameter | Description |
|---|---|
| `riid` | Interface identifier to return. Use `IID_PPV_ARGS` to request `IWinMLRuntime`. |
| `runtime` | Receives the created interface pointer. |

**Returns:** `S_OK` on success. Returns `E_POINTER` when `runtime` is null and `E_NOINTERFACE` when `riid` is not supported.

```cpp
wil::com_ptr<IWinMLRuntime> runtime;
THROW_IF_FAILED(WinMLCreateRuntime(IID_PPV_ARGS(runtime.put())));
```

---

## WinMLCreateTasks

```cpp
STDAPI WinMLCreateTasks(
    IWinMLRuntime* runtime,
    REFIID interfaceId,
    void** tasks
);
```

Creates a Task API entry object over the supplied Runtime.

| Parameter | Description |
|---|---|
| `runtime` | Runtime instance used by the created Task object. |
| `interfaceId` | Interface identifier to return, such as `IID_IWinMLTasks` or a Task factory interface. |
| `tasks` | Receives the created interface pointer. |

**Returns:** `S_OK` on success. Returns `E_POINTER` when `runtime` or `tasks` is null. Unsupported interface IDs fail through the Task runtime factory.

See [IWinMLTasks](IWinMLTasks.md) for Task activation.

---

## Target-owned and object-owned factory methods

Other factory methods are exposed on Runtime objects rather than as exported functions:

- `IWinMLRuntime::CreatePipelineBuilder`, `CreateCpuExecutionTarget`, `CreateExecutionTargetFromAdapter`, `CreateExecutionTargetFromD3D12`, and `CreateExecutionTarget`
- tensor factory interfaces obtained with `QueryInterface` from an `IWinMLExecutionTarget`
- `IWinMLD3D12ExecutionTarget::CreateFence`
- `IWinMLModelCompiler`, obtained with `QueryInterface` from an `IWinMLExecutionTarget`

---

## WinMLCreateTokenizerFromFile

```cpp
STDAPI WinMLCreateTokenizerFromFile(
    LPCWSTR configFilePath,
    IWinMLTokenizer** tokenizer
);
```

Creates a tokenizer from a tokenizer configuration file.

| Parameter | Description |
|---|---|
| `configFilePath` | Path to `tokenizer.json`. |
| `tokenizer` | Receives the created tokenizer interface. |

**Returns:** `S_OK` on success. Returns `E_POINTER` when `configFilePath` or `tokenizer` is null. File and tokenizer-format failures are returned as `HRESULT`s from initialization.

---

## Server activation

`WinMLServer.dll` doesn't export a factory function. Create a server with
`winml::server::Create` from `winml/server/WinMLServer.hpp`, or from C with
`WinMLLoadServer` from `winml/server/WinMLServerActivation.h`. Both helpers load
the DLL and get `IWinMLServerFactory` from its `DllGetClassObject`. See
[IWinMLServer](IWinMLServer.md#activation).

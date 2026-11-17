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

Creates a tokenizer from a model's tokenizer files.

| Parameter | Description |
|---|---|
| `configFilePath` | Path to a Hugging Face checkpoint folder, its `tokenizer.json`, its `tokenizer_config.json`, or a `.gguf` file. |
| `tokenizer` | Receives the created tokenizer interface. |

A `tokenizer.json` or `tokenizer_config.json` is read with the other file beside
it. A `.gguf` file's byte-level BPE or SentencePiece vocabulary is read from the
file's metadata and served without llama.cpp; the chat template is the file's
`tokenizer.chat_template`. Other GGUF vocabularies, and files whose chat template
names a built-in llama.cpp format, are served by the llama.cpp backend when its
payload is deployed.

**Returns:** `S_OK` on success. Returns `E_POINTER` when `configFilePath` or `tokenizer` is null, `HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)` when the tokenizer files are not found, `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` for a GGUF vocabulary that cannot be represented without llama.cpp when llama.cpp is not available, and `HRESULT_FROM_WIN32(ERROR_INVALID_DATA)` for malformed GGUF tokenizer metadata. Other file and tokenizer-format failures are returned as `HRESULT`s from initialization.

---

## Server activation

`WinMLServer.dll` doesn't export a factory function. Create a server with
`winml::server::Create` from `winml/server/WinMLServer.hpp`, or from C with
`WinMLLoadServer` from `winml/server/WinMLServerActivation.h`. Both helpers load
the DLL and get `IWinMLServerFactory` from its `DllGetClassObject`. See
[IWinMLServer](IWinMLServer.md#activation).

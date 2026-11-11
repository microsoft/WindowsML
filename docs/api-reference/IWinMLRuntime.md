<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLRuntime

> Part of the [WinML Runtime API Reference](README.md).
> See [Common Patterns](CommonPatterns.md) for API usage examples.

**Process-local runtime entry point.**

Create the runtime through [`WinMLCreateRuntime`](FactoryFunctions.md). `IWinMLRuntime` loads model artifacts, creates pipeline builders, and creates execution targets.

File loading recognizes the artifact from the file extension, case-insensitively. In a pipeline, `.onnx` and `.ort` models run on the ONNX Runtime backend, `.gguf` models run on the llama.cpp backend when its payload is deployed, and `.oir`, `.mlirbc`, and `.mlir` artifacts run on the CGC backend. Buffer loading recognizes ORT flatbuffers by the `ORTM` identifier; other supported buffer artifacts, including CGC artifacts, are detected by their bytes. A loaded model is still only an artifact handle. Pipeline `Build` prepares the model for the selected execution target.

`LoadModelFromPath` also loads a model folder, a directory with `config.json` at its root, for the Models-backed Task entry points: `IWinMLModelTextGenerationConfiguration::SetModel`, `IWinMLModelAutomaticSpeechRecognitionConfiguration::SetModel`, and `IWinMLDecisionTaskFactory::CreateDecisionTaskFromModel`. Those entry points run a model according to how it was loaded. A model loaded with `LoadModelFromPath` or `LoadModelFromFile` is planned by Windows ML for the CGC backend and runs on the GPU target the application picks, never on llama.cpp. To run a GGUF model on llama.cpp from a Task, load it with [`IWinMLLlamaModelLoader`](IWinMLLlamaModelLoader.md), which is available by `QueryInterface` on `IWinMLRuntime`.

Declared tensor metadata, when available, is exposed through `IWinMLModelSchema`. Bindings use ordinal input and output indexes. Query a model for [`IWinMLModelWeightFormats`](IWinMLModelWeightFormats.md) to read how its weights are stored.

```
IID: 6954707d-3987-491a-ada9-2bea9b0e13f9
```

## `LoadModelFromFile`

```cpp
HRESULT LoadModelFromFile(
    [in, string] LPCWSTR path,
    [in, unique] IWinMLResourceMapReader* resources,
    [out, retval] IWinMLModel** model
);
```

Loads a model artifact from `path`.

| Parameter | Description |
|---|---|
| `path` | Path to the model artifact to load. |
| `resources` | Resource map used when the artifact references external resources, or `nullptr`. |
| `model` | Receives the loaded model. |

**Returns:** `S_OK` on success. Returns `E_POINTER` when `path` or `model` is null. If the path does not name a regular file, the method returns the corresponding file-system failure or `E_INVALIDARG` for a directory; use `LoadModelFromPath` for a model folder. Unsupported file extensions return `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`. A CGC artifact in text form stamped with a CGC IR version that this version cannot read returns `D3D12_ERROR_DRIVER_VERSION_MISMATCH`; compile it again from the source model. CGC bytecode from older IR versions is upgraded when it loads.

A GGUF file loads even where no backend in the package runs GGUF models. Such a model is then for the Models-backed Task entry points only, and `IWinMLPipelineBuilder::AddModelStage` returns `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` for it. Where the llama.cpp backend is included but its payload is not deployed, loading and `AddModelStage` succeed and `Build` returns `HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)`.

---

## `LoadModelFromPath`

```cpp
HRESULT LoadModelFromPath(
    [in, string] LPCWSTR path,
    [in, unique] IWinMLResourceMapReader* resources,
    [out, retval] IWinMLModel** model
);
```

Loads the model at `path`: a model file, loaded exactly as `LoadModelFromFile` loads it, or a model folder, a directory with a file named `config.json` at its root.

| Parameter | Description |
|---|---|
| `path` | Path to a model file or a model folder. |
| `resources` | Resource map for a model file, as for `LoadModelFromFile`. Must be `nullptr` for a folder. |
| `model` | Receives the loaded model. |

**Returns:**

| HRESULT | Condition |
|---|---|
| `S_OK` | The model was loaded. |
| `E_POINTER` | `path` or `model` is null. |
| `E_INVALIDARG` | `resources` is not null and `path` names a folder. |
| `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` | `path` names a folder that has no file named `config.json` at its root. |
| A file-system error | The attributes of `path`, or of a folder's `config.json`, cannot be read. |
| The results of `LoadModelFromFile` | `path` names a file or does not exist. |

Loading a folder opens no file. A folder is a model for the Models-backed Task entry points, which read it: `IWinMLPipelineBuilder::AddModelStage` and `IWinMLModelCompiler` return `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` for it, and `IWinMLModelSchema` reports no declared tensors.

---

## `LoadModelFromBuffer`

```cpp
HRESULT LoadModelFromBuffer(
    [in, size_is(byteCount)] const BYTE* bytes,
    [in] UINT64 byteCount,
    [in, unique] IWinMLResourceMapReader* resources,
    [out, retval] IWinMLModel** model
);
```

Loads a model artifact from an in-memory buffer.

| Parameter | Description |
|---|---|
| `bytes` | Pointer to the artifact bytes. |
| `byteCount` | Size of `bytes`, in bytes. |
| `resources` | Resource map used when the artifact references external resources, or `nullptr`. |
| `model` | Receives the loaded model. |

**Returns:** `S_OK` on success. Returns `E_POINTER` when `bytes` or `model` is null and `E_INVALIDARG` when `byteCount` is zero or cannot fit in memory on the current process.

---

## Python model loading

```python
source = runtime.load_model("model.onnx")
checkpoint = runtime.load_model("models\\qwen2.5-0.5b-instruct")  # a model folder
compiled = runtime.load_model(compiled_path, resources=resource_map)
from_buffer = runtime.load_model_from_buffer(artifact_bytes, resources=resource_map)
llama_model = runtime.load_llama_model("model.gguf")  # IWinMLLlamaModelLoader
```

`resources` is keyword-only in the Python projection. `load_model` calls `LoadModelFromPath`, so it loads a file or a model folder; a folder with `resources` raises `ValueError`. `load_llama_model` raises `NotSupportedError` when the package has no llama.cpp backend.

---

## `CreatePipelineBuilder`

```cpp
HRESULT CreatePipelineBuilder(
    [out, retval] IWinMLPipelineBuilder** builder
);
```

Creates a pipeline builder.

| Parameter | Description |
|---|---|
| `builder` | Receives the pipeline builder. |

**Returns:** `S_OK` on success. Returns `E_POINTER` when `builder` is null.

---

## `CreateCpuExecutionTarget`

```cpp
HRESULT CreateCpuExecutionTarget(
    [out, retval] IWinMLExecutionTarget** target
);
```

Creates a CPU execution target.

| Parameter | Description |
|---|---|
| `target` | Receives the execution target. |

**Returns:** `S_OK` on success. Returns `E_POINTER` when `target` is null.

---

## `CreateExecutionTargetFromAdapter`

```cpp
HRESULT CreateExecutionTargetFromAdapter(
    [in] IUnknown* dxCoreAdapter,
    [out, retval] IWinMLExecutionTarget** target
);
```

Creates an execution target from a DXCore adapter object.

| Parameter | Description |
|---|---|
| `dxCoreAdapter` | Adapter object that identifies the hardware target. |
| `target` | Receives the execution target. |

**Returns:** `S_OK` on success. Returns `E_POINTER` when `dxCoreAdapter` or `target` is null. Adapter-query failures are returned from the underlying DXCore calls.

---

## `CreateExecutionTargetFromD3D12`

```cpp
HRESULT CreateExecutionTargetFromD3D12(
    [in] IUnknown* d3d12Device,
    [in, unique] IUnknown* d3d12CommandQueue,
    [out, retval] IWinMLExecutionTarget** target
);
```

Creates an execution target from an existing D3D12 device and command queue, or from a D3D12 device alone.

| Parameter | Description |
|---|---|
| `d3d12Device` | D3D12 device object to associate with the target. |
| `d3d12CommandQueue` | D3D12 command queue to associate with the target, or `nullptr`. |
| `target` | Receives the execution target. |

**Returns:** `S_OK` on success. Returns `E_POINTER` when `d3d12Device` or `target` is null. Device initialization failures are returned as `HRESULT`s.

---

## `CreateExecutionTarget`

```cpp
HRESULT CreateExecutionTarget(
    [in] WINML_EXECUTION_TARGET_KIND kind,
    [in] WINML_EXECUTION_TARGET_PREFERENCE preference,
    [out, retval] IWinMLExecutionTarget** target
);
```

Creates an execution target for a hardware class without requiring the caller to enumerate devices.

| Parameter | Description |
|---|---|
| `kind` | Hardware class to target. See [WINML_EXECUTION_TARGET_KIND](Enumerations.md). |
| `preference` | Selection preference for devices of that class. See [WINML_EXECUTION_TARGET_PREFERENCE](Enumerations.md). |
| `target` | Receives the execution target. |

**Returns:**

| HRESULT | Condition |
|---|---|
| `S_OK` | A target for the requested class was created. |
| `E_POINTER` | `target` was null. |
| `E_INVALIDARG` | `kind` or `preference` is outside the defined enumeration values. |
| `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` | The platform cannot enumerate devices. |
| `HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` | Enumeration succeeded, but no device of the requested class is present. |

`WINML_EXECUTION_TARGET_KIND_CPU` is equivalent to `CreateCpuExecutionTarget`. GPU and NPU select a hardware device of that class and bind the target to it, equivalent to passing that device to `CreateExecutionTargetFromAdapter`, with one exception for GGUF model stages, described below.

The preference expresses selection intent, not a guarantee. It orders candidates of the requested class when the platform can order them, and is ignored when it cannot or when only one candidate exists. The preference never widens the search to another hardware class and does not change the class reported by `IWinMLExecutionTarget::GetKind`.

A GGUF model stage treats a GPU target from this method as a preference for the selected device. When no available llama.cpp GPU backend serves the selected device, the device cannot be identified by its PCI location, or it cannot hold any layer of the model, `Build` can place the model on another GPU, and the stage then reports a target for that adapter through `IWinMLStage::GetExecutionTarget`. Use `CreateExecutionTargetFromAdapter` or `CreateExecutionTargetFromD3D12` when a GGUF stage must use one specific device; `Build` then returns `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` when no GPU backend is available, `HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` when none serves the device or, on a system with more than one hardware adapter, the device cannot be identified by its PCI location, and `HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_MEMORY)` when the device cannot hold any layer of the model.

Selecting a target does not commit to model compatibility; `IWinMLPipelineBuilder::Build` remains the compatibility commit point. A caller that can fall back to another class should treat `ERROR_NOT_FOUND` and `ERROR_NOT_SUPPORTED` alike and request a different `kind`.

```cpp
wil::com_ptr<IWinMLExecutionTarget> target;
HRESULT hr = runtime->CreateExecutionTarget(
    WINML_EXECUTION_TARGET_KIND_NPU,
    WINML_EXECUTION_TARGET_PREFERENCE_EFFICIENCY,
    target.put());
if (hr == HRESULT_FROM_WIN32(ERROR_NOT_FOUND) ||
    hr == HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED))
{
    RETURN_IF_FAILED(runtime->CreateExecutionTarget(
        WINML_EXECUTION_TARGET_KIND_GPU,
        WINML_EXECUTION_TARGET_PREFERENCE_PERFORMANCE,
        target.put()));
}
else
{
    RETURN_IF_FAILED(hr);
}
```

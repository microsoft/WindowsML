<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLRuntime

> Part of the [WinML Runtime API Reference](README.md).
> See [Common Patterns](CommonPatterns.md) for API usage examples.

**Process-local runtime entry point.**

Create the runtime through [`WinMLCreateRuntime`](FactoryFunctions.md). `IWinMLRuntime` loads model artifacts, creates pipeline builders, and creates execution targets.

File loading selects the backend from the file extension, case-insensitively: `.onnx` and `.ort` use the ONNX Runtime backend, and `.gguf` uses the llama.cpp backend. Buffer loading recognizes ORT flatbuffers by the `ORTM` identifier; other supported buffer artifacts are detected by their bytes. A loaded model is still only an artifact handle. Pipeline `Build` prepares the model for the selected execution target.

Declared tensor metadata, when available, is exposed through `IWinMLModelSchema`. Bindings use ordinal input and output indexes.

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

**Returns:** `S_OK` on success. Returns `E_POINTER` when `path` or `model` is null. If the path does not name a regular file, the method returns the corresponding file-system failure or `E_INVALIDARG` for a directory. Unsupported file extensions return `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`.

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
compiled = runtime.load_model(compiled_path, resources=resource_map)
from_buffer = runtime.load_model_from_buffer(artifact_bytes, resources=resource_map)
```

`resources` is keyword-only in the Python projection.

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

`WINML_EXECUTION_TARGET_KIND_CPU` is equivalent to `CreateCpuExecutionTarget`. GPU and NPU select a hardware device of that class and bind the target to it, equivalent to passing that device to `CreateExecutionTargetFromAdapter`.

The preference expresses selection intent, not a guarantee. It orders candidates of the requested class when the platform can order them, and is ignored when it cannot or when only one candidate exists. The preference never widens the search to another hardware class and does not change the class reported by `IWinMLExecutionTarget::GetKind`.

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

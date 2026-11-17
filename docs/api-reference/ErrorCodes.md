<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Error Codes

> Part of the [WinML Runtime API Reference](README.md).

WinML methods return `HRESULT`. The table lists common values returned by the Runtime APIs in this reference.

| HRESULT | Meaning |
|---|---|
| `S_OK` | Success. |
| `E_POINTER` | A required pointer parameter was null. |
| `E_INVALIDARG` | A parameter value was invalid, such as an out-of-range enum value, a directory passed as a model path, a zero-length model buffer, or a null D3D12 fence passed to `CreateFence`. |
| `E_NOINTERFACE` | A requested COM interface is not supported by the object. |
| `E_NOT_VALID_STATE` | The object state does not allow the operation, such as requesting a D3D12 fence from a pre-signaled `IWinMLFence`. |
| `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` | The requested operation or artifact type is not supported, such as an unknown model file extension, D3D12 accessors on a CPU execution target, a model that a Models-backed Task entry point does not support, or a model folder passed to `AddModelStage`. |
| `HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` | No device of the requested hardware class was found. |
| `HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)` | A file path did not resolve to an existing file, or a llama.cpp model was built or served without the llama.cpp payload deployed. |
| `HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND)` | The files of a loaded model no longer exist when a Models-backed Task entry point reads them. |
| `HRESULT_FROM_WIN32(ERROR_BAD_FORMAT)` | A model file cannot be parsed, or a model folder lacks a file it requires. |
| `HRESULT_FROM_WIN32(ERROR_INVALID_DATA)` | A model file is malformed, or a stateful stage has no sequence capacity (set a capacity hint). |
| `HRESULT_FROM_WIN32(ERROR_ACCESS_DISABLED_BY_POLICY)` | A text generation request disabled a watermark that its configuration requires. |
| `HRESULT_FROM_WIN32(ERROR_BUSY)` | A pipeline or stage binding was mutated while execution was active, or execution was started while the same pipeline was already active. |
| `HRESULT_FROM_WIN32(WAIT_TIMEOUT)` | `IWinMLFence::Wait` timed out before the completion value was reached. |
| `D3D12_ERROR_DRIVER_VERSION_MISMATCH` | A CGC artifact in text form was stamped with a CGC IR version that this version of Windows ML cannot read. Compile it again from the source model. CGC bytecode from older IR versions is upgraded when it loads. |

Backend-specific parsing, compilation, and execution failures are returned as `HRESULT`s from the selected backend. When ONNX Runtime refuses to create a session, `IWinMLPipelineBuilder::Build` returns the `HRESULT` that corresponds to ONNX Runtime's error and publishes its message through `IErrorInfo` (Windows ML 2.7 returned `HRESULT_FROM_WIN32(ERROR_UNHANDLED_EXCEPTION)`); Python includes the message in `WinMLError`. Check the method reference for argument validation performed before backend calls.

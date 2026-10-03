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
| `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` | The requested operation or artifact type is not supported, such as an unknown model file extension or D3D12 accessors on a CPU execution target. |
| `HRESULT_FROM_WIN32(ERROR_NOT_FOUND)` | No device of the requested hardware class was found. |
| `HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)` | A file path did not resolve to an existing file. |
| `HRESULT_FROM_WIN32(ERROR_BUSY)` | A pipeline or stage binding was mutated while execution was active, or execution was started while the same pipeline was already active. |
| `HRESULT_FROM_WIN32(WAIT_TIMEOUT)` | `IWinMLFence::Wait` timed out before the completion value was reached. |

Backend-specific parsing, compilation, and execution failures are returned as `HRESULT`s from the selected backend. Check the method reference for argument validation performed before backend calls.

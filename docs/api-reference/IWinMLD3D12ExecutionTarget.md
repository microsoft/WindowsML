<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLD3D12ExecutionTarget

> Part of the [WinML Runtime API Reference](README.md).

**D3D12 target capability.**

Query this interface from `IWinMLExecutionTarget` when D3D12 interop is required. CPU targets return `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` from the D3D12 device and queue accessors.

```
IID: b243d097-07d0-4f3c-8519-370df6321e43
```

## `GetDevice`

```cpp
HRESULT GetDevice([out, retval] IUnknown** d3d12Device);
```

Returns the target's D3D12 device object.

| Parameter | Description |
|---|---|
| `d3d12Device` | Receives the D3D12 device as `IUnknown*`. |

**Returns:** `S_OK` on success. Returns `E_POINTER` when `d3d12Device` is null. CPU targets return `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` from the implementation that owns this capability.

---

## `GetCommandQueue`

```cpp
HRESULT GetCommandQueue([out, retval] IUnknown** d3d12CommandQueue);
```

Returns the target's D3D12 command queue object.

| Parameter | Description |
|---|---|
| `d3d12CommandQueue` | Receives the D3D12 command queue as `IUnknown*`. |

**Returns:** `S_OK` on success. Returns `E_POINTER` when `d3d12CommandQueue` is null. CPU targets return `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` from the implementation that owns this capability.

---

## `CreateFence`

```cpp
HRESULT CreateFence(
    [in] IUnknown* d3d12Fence,
    [in] UINT64 completionValue,
    [out, retval] IWinMLFence** fence);
```

Wraps a D3D12 fence and completion value as an `IWinMLFence`.

| Parameter | Description |
|---|---|
| `d3d12Fence` | D3D12 fence object to wrap. |
| `completionValue` | Completion value that marks the fence as satisfied. |
| `fence` | Receives the wrapped `IWinMLFence`. |

**Returns:** `S_OK` on success. Returns `E_POINTER` when `fence` is null and `E_INVALIDARG` when `d3d12Fence` is null. `QueryInterface` or event-creation failures are returned as `HRESULT`s.

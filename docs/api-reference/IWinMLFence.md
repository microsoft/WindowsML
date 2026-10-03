<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLFence

> Part of the [WinML Runtime API Reference](README.md).

**Completion fence.**

An `IWinMLFence` either wraps a D3D12 fence and completion value or represents already-completed work.

```
IID: 045746c4-bc82-4390-8886-aa5cf6ad1e0e
```

## `Wait`

```cpp
HRESULT Wait([in] UINT32 timeoutMilliseconds);
```

Blocks until the fence reaches its completion value or the timeout expires.

| Parameter | Description |
|---|---|
| `timeoutMilliseconds` | Timeout, in milliseconds. Use `INFINITE` for an unbounded wait. |

**Returns:** `S_OK` when the fence is complete. Returns `HRESULT_FROM_WIN32(WAIT_TIMEOUT)` when the timeout expires.

---

## `IsComplete`

```cpp
HRESULT IsComplete([out, retval] BOOL* complete);
```

Checks whether the fence has completed without blocking.

| Parameter | Description |
|---|---|
| `complete` | Receives `TRUE` when the fence has completed, otherwise `FALSE`. |

**Returns:** `S_OK` on success. Returns `E_POINTER` when `complete` is null.

---

## `GetD3D12Fence`

```cpp
HRESULT GetD3D12Fence([out, retval] IUnknown** fence);
```

Returns the underlying D3D12 fence object.

| Parameter | Description |
|---|---|
| `fence` | Receives the underlying fence as `IUnknown*`. |

**Returns:** `S_OK` on success. Returns `E_POINTER` when `fence` is null and `E_NOT_VALID_STATE` when the `IWinMLFence` is already completed and has no D3D12 fence object.

---

## `GetCompletionValue`

```cpp
HRESULT GetCompletionValue([out, retval] UINT64* value);
```

Returns the completion value associated with this fence.

| Parameter | Description |
|---|---|
| `value` | Receives the completion value. |

**Returns:** `S_OK` on success. Returns `E_POINTER` when `value` is null.

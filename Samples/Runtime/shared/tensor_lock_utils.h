// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Tensor lock helpers for Windows ML Runtime samples.
//
// Wraps IWinMLTensor::Lock, IWinMLTensorDataLock, and synchronized CPU access so
// samples can read CPU-resident and device-resident tensors through one path.

#pragma once

#include <windows.h>
#include <wrl/client.h>

#include <WinMLRuntime.h>

struct LockedTensorData
{
    Microsoft::WRL::ComPtr<IWinMLTensorDataLock> lock;
    BYTE* data = nullptr;
    UINT64 size = 0;

    template <typename T>
    T* As() const noexcept
    {
        return reinterpret_cast<T*>(data);
    }
};

// Opens a CPU-access window on a tensor and returns the pointer owned by the
// IWinMLTensorDataLock lifetime.
inline HRESULT LockTensorData(IWinMLTensor* tensor, WINML_TENSOR_LOCK_MODE mode,
                              WINML_TENSOR_LOCK_FLAGS flags, LockedTensorData* lockedData) noexcept
{
    if (!tensor || !lockedData)
    {
        return E_POINTER;
    }

    lockedData->lock.Reset();
    lockedData->data = nullptr;
    lockedData->size = 0;

    HRESULT hr = tensor->Lock(mode, flags, lockedData->lock.GetAddressOf());
    if (FAILED(hr))
    {
        return hr;
    }

    hr = lockedData->lock->GetData(&lockedData->data, &lockedData->size);
    if (FAILED(hr))
    {
        lockedData->lock.Reset();
        lockedData->data = nullptr;
        lockedData->size = 0;
        return hr;
    }

    return S_OK;
}

// Attempts a direct CPU read lock; this succeeds for CPU-resident tensors.
inline HRESULT LockTensorForRead(IWinMLTensor* tensor, LockedTensorData* lockedData) noexcept
{
    return LockTensorData(tensor, WINML_TENSOR_LOCK_MODE_READ, WINML_TENSOR_LOCK_FLAG_NONE,
                          lockedData);
}

// Requests synchronized CPU access, allowing Runtime-managed transfer for
// device-resident tensors before returning the pointer.
inline HRESULT LockTensorForReadWithSynchronization(IWinMLTensor* tensor,
                                                    LockedTensorData* lockedData) noexcept
{
    return LockTensorData(tensor, WINML_TENSOR_LOCK_MODE_READ,
                          WINML_TENSOR_LOCK_FLAG_ALLOW_SYNCHRONIZED_CPU_ACCESS, lockedData);
}

// Reads from either CPU-resident or device-resident tensors, falling back to the
// synchronized path only when a direct CPU lock is not supported.
inline HRESULT LockTensorForReadAny(IWinMLTensor* tensor, LockedTensorData* lockedData) noexcept
{
    // Prefer a plain CPU read first. If the tensor lives on an accelerator, the
    // Runtime may require an explicit synchronized lock so it can copy data back
    // to CPU-visible memory before returning the pointer.
    HRESULT hr = LockTensorForRead(tensor, lockedData);
    if (hr != HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED))
    {
        return hr;
    }

    return LockTensorForReadWithSynchronization(tensor, lockedData);
}

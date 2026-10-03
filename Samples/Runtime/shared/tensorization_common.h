// Copyright (C) Microsoft Corporation. All rights reserved.
//
// tensorization_common.h - Runtime plumbing shared by the image and video samples.
//
// Wraps the Runtime API setup that the WIC and Media Foundation samples share: COM apartment
// lifetime, IWinMLRuntime creation, CPU target creation, inference target selection, and
// synchronized tensor readback.

#pragma once

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <vector>

#include <Windows.h>
#include <objbase.h>

#include <WinMLRuntime.h>
#include <WinMLTensor.h>

#include "common.h" // ComPtr, CHECK_HR, CHECK_HR_MSG
#include "execution_target_utils.h"
#include "tensor_lock_utils.h" // LockedTensorData, LockTensorForReadAny

namespace winmlsamples
{
namespace tensors
{

// ---------------------------------------------------------------------------
// COM apartment
// ---------------------------------------------------------------------------

// Initializes a COM apartment for the calling thread for the lifetime of the
// scope. The WIC / GDI / Media Foundation / WASAPI subsystems these samples
// borrow are COM based and require an initialized apartment on the calling
// thread; the WinML runtime does not initialize COM for you.
// A prior initialization with a different model (RPC_E_CHANGED_MODE) is tolerated
// so a sample can run inside a host that already chose an apartment.
struct ComApartment
{
    explicit ComApartment(DWORD model = COINIT_MULTITHREADED) noexcept
    {
        hr = CoInitializeEx(nullptr, model);
        if (hr == RPC_E_CHANGED_MODE)
        {
            hr = S_OK;
        }
        else
        {
            initialized = SUCCEEDED(hr);
        }
    }

    ~ComApartment() noexcept
    {
        if (initialized)
        {
            CoUninitialize();
        }
    }

    ComApartment(const ComApartment&) = delete;
    ComApartment& operator=(const ComApartment&) = delete;

    HRESULT hr = S_OK;
    bool initialized = false;
};

// Creates the Runtime and a CPU execution target. Every tensorization adapter
// takes an IWinMLExecutionTarget as its first argument; the target owns tensor
// memory and exposes the tensor factories the adapters route through.
inline HRESULT CreateCpuRuntimeAndDevice(_COM_Outptr_ IWinMLRuntime** runtime,
                                         _COM_Outptr_ IWinMLExecutionTarget** target) noexcept
{
    if (runtime == nullptr || target == nullptr)
    {
        return E_POINTER;
    }

    *runtime = nullptr;
    *target = nullptr;

    ComPtr<IWinMLRuntime> localRuntime;
    HRESULT hr = WinMLCreateRuntime(IID_PPV_ARGS(localRuntime.GetAddressOf()));
    if (FAILED(hr))
    {
        return hr;
    }

    ComPtr<IWinMLExecutionTarget> localTarget;
    hr = localRuntime->CreateCpuExecutionTarget(localTarget.GetAddressOf());
    if (FAILED(hr))
    {
        return hr;
    }

    *runtime = localRuntime.Detach();
    *target = localTarget.Detach();
    return S_OK;
}

inline HRESULT CreateExecutionTarget(_In_ IWinMLRuntime* runtime, const DeviceArgs& device,
                                     _COM_Outptr_ IWinMLExecutionTarget** target,
                                     _COM_Outptr_opt_ IWinMLExecutionTarget** tensorTarget) noexcept
{
    return ::CreateExecutionTarget(runtime, device, target, tensorTarget);
}

inline HRESULT CreateExecutionTarget(_In_ IWinMLRuntime* runtime, const DeviceArgs& device,
                                     _COM_Outptr_ IWinMLExecutionTarget** target) noexcept
{
    return ::CreateExecutionTarget(runtime, device, target);
}

// ---------------------------------------------------------------------------
// Inference device selection
// ---------------------------------------------------------------------------

// Tensorization always runs on the CPU target in these samples (see
// CreateCpuRuntimeAndDevice). The inference pipeline is an independent choice:
// the CPU-resident tensor an adapter produces can feed a pipeline that runs on
// the CPU, GPU, or NPU, and the Runtime moves the data across the device
// boundary. These helpers retarget only the inference pipeline from the shared
// --device/--performance/--efficiency arguments. The device defaults to CPU.
//
// An explicit --device gpu|npu is a real request, not a hint. The Runtime
// returns an error rather than silently dropping to CPU.
struct InferenceOptions
{
    DeviceArgs device;    // device.deviceType defaults to WINML_EXECUTION_TARGET_KIND_CPU
    bool verbose = false; // --verbose: raise ONNX Runtime logging to show node placement
};

inline InferenceOptions ParseInferenceOptions(int argc, wchar_t* argv[]) noexcept
{
    InferenceOptions options;
    options.device = ParseDeviceArgs(argc, argv);
    options.verbose = HasArg(argc, argv, L"--verbose");
    return options;
}

// Human-readable name for the execution policy, so a sample can show that
// --performance / --efficiency actually changed the request.
inline const wchar_t* ExecutionPolicyName(DeviceArgs::ExecutionPolicy policy) noexcept
{
    switch (policy)
    {
    case DeviceArgs::ExecutionPolicy::PreferEfficiency:
        return L"efficiency";
    case DeviceArgs::ExecutionPolicy::PreferPerformance:
        return L"performance";
    default:
        return L"default";
    }
}

// Shared --help / usage for the device-selectable samples. Returns true when
// the caller passed --help or -h, in which case the caller should return 0.
// positionalHint is printed before the flags (e.g. L"[model.onnx] ") or L"".
inline bool ShowInferenceUsageIfRequested(int argc, wchar_t* argv[], const wchar_t* exeName,
                                          const wchar_t* positionalHint,
                                          const wchar_t* additionalOptions = nullptr) noexcept
{
    if (!HasArg(argc, argv, L"--help") && !HasArg(argc, argv, L"-h"))
    {
        return false;
    }

    std::wprintf(
        L"Usage: %s %s[--device cpu|gpu|npu] [--ep <name>] [--performance|--efficiency] [--verbose]\n",
        exeName, positionalHint);
    std::wprintf(
        L"  Tensorization always runs on CPU; --device selects the inference device only.\n");
    std::wprintf(L"  --ep <name> pins one installed catalog provider to the requested\n");
    std::wprintf(L"  hardware class.\n");
    std::wprintf(L"  --performance/--efficiency steer GPU adapter selection when multiple\n");
    std::wprintf(L"  adapters are present.\n");
    if (additionalOptions != nullptr)
    {
        std::wprintf(L"%s", additionalOptions);
    }

    return true;
}

// ---------------------------------------------------------------------------
// Tensor read-back
// ---------------------------------------------------------------------------

// Reads the first `count` float elements of a tensor into `out` through the
// residence-agnostic lock helper (plain CPU lock first, synchronized fallback
// for device-resident tensors). Returns false if the tensor is too small to
// satisfy the request or cannot be mapped.
inline bool ReadTensorFloats(IWinMLTensor* tensor, UINT64 count, std::vector<float>& out) noexcept
{
    if (tensor == nullptr || count == 0 || count > static_cast<UINT64>(SIZE_MAX / sizeof(float)))
    {
        return false;
    }

    const size_t byteCount = static_cast<size_t>(count) * sizeof(float);

    out.assign(static_cast<size_t>(count), 0.0f);

    LockedTensorData locked;
    if (FAILED(LockTensorForReadAny(tensor, &locked)))
    {
        return false;
    }

    if (locked.data == nullptr || locked.size < byteCount)
    {
        return false;
    }

    std::memcpy(out.data(), locked.data, byteCount);
    return true;
}

// ---------------------------------------------------------------------------

} // namespace tensors
} // namespace winmlsamples

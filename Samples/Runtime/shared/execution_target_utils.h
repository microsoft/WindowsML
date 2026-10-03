// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Shared execution-target creation for Windows ML Runtime samples.
//
// Wraps IWinMLRuntime CPU/GPU/NPU target creation, optional
// IWinMLOrtCompatibility provider pinning, and DXCore adapter selection used by
// samples with --device, --ep, --performance, and --efficiency.

#pragma once

#include <cstdint>
#include <vector>

#include <initguid.h>
#include <dxcore.h>
#include <dxcore_interface.h>
#include <wrl/client.h>

#include <WinMLRuntime.h>
#include <WinMLRuntimeOrt.h>

#include "common.h"
#include "ep_catalog_utils.h"

// Ranks hardware adapters and returns the best match for the requested policy.
//
// allowedHardwareIds, when non-empty, restricts the search to adapters a pinned
// execution provider can actually drive. Ranking within that set is unchanged.
inline HRESULT FindHardwareAdapter(REFGUID attribute, DeviceArgs::ExecutionPolicy policy,
                                   const std::vector<EpHardwareId>& allowedHardwareIds,
                                   _COM_Outptr_ IDXCoreAdapter** outAdapter) noexcept
{
    if (outAdapter == nullptr)
    {
        return E_POINTER;
    }

    *outAdapter = nullptr;

    ComPtr<IDXCoreAdapterFactory> factory;
    CHECK_HR(DXCoreCreateAdapterFactory(IID_PPV_ARGS(factory.GetAddressOf())));

    ComPtr<IDXCoreAdapterList> adapterList;
    CHECK_HR(factory->CreateAdapterList(1, &attribute, IID_PPV_ARGS(adapterList.GetAddressOf())));

    ComPtr<IDXCoreAdapter> bestAdapter;
    uint64_t bestDedicatedMemory = 0;
    bool bestIsIntegrated = false;
    bool excludedByProvider = false;

    for (uint32_t i = 0; i < adapterList->GetAdapterCount(); ++i)
    {
        ComPtr<IDXCoreAdapter> adapter;
        if (FAILED(adapterList->GetAdapter(i, IID_PPV_ARGS(adapter.GetAddressOf()))))
        {
            continue;
        }

        bool isHardware = false;
        if (FAILED(adapter->GetProperty(DXCoreAdapterProperty::IsHardware, &isHardware)) ||
            !isHardware)
        {
            continue;
        }

        if (!allowedHardwareIds.empty())
        {
            DXCoreHardwareID hardwareId = {};
            if (FAILED(adapter->GetProperty(DXCoreAdapterProperty::HardwareID, &hardwareId)))
            {
                continue;
            }

            bool providerCanUseAdapter = false;
            for (const EpHardwareId& allowed : allowedHardwareIds)
            {
                if (allowed.vendorId == hardwareId.vendorID &&
                    allowed.deviceId == hardwareId.deviceID)
                {
                    providerCanUseAdapter = true;
                    break;
                }
            }

            if (!providerCanUseAdapter)
            {
                excludedByProvider = true;
                continue;
            }
        }

        uint64_t dedicatedMemory = 0;
        bool isIntegrated = false;
        adapter->GetProperty(DXCoreAdapterProperty::DedicatedAdapterMemory, &dedicatedMemory);
        adapter->GetProperty(DXCoreAdapterProperty::IsIntegrated, &isIntegrated);

        bool isBetter = !bestAdapter;
        if (bestAdapter && policy == DeviceArgs::ExecutionPolicy::PreferEfficiency)
        {
            isBetter = (isIntegrated && !bestIsIntegrated) ||
                       (isIntegrated == bestIsIntegrated && dedicatedMemory < bestDedicatedMemory);
        }
        else if (bestAdapter)
        {
            isBetter = dedicatedMemory > bestDedicatedMemory;
        }

        if (isBetter)
        {
            bestAdapter = adapter;
            bestDedicatedMemory = dedicatedMemory;
            bestIsIntegrated = isIntegrated;
        }
    }

    if (!bestAdapter)
    {
        if (excludedByProvider)
        {
            wprintf(L"  ERROR: the pinned execution provider exposes no adapter "
                    L"that this device class can use.\n");
        }

        return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    }

    size_t descriptionSize = 0;
    if (SUCCEEDED(bestAdapter->GetPropertySize(DXCoreAdapterProperty::DriverDescription,
                                               &descriptionSize)) &&
        descriptionSize > 1)
    {
        std::vector<char> description(descriptionSize);
        if (SUCCEEDED(bestAdapter->GetProperty(DXCoreAdapterProperty::DriverDescription,
                                               descriptionSize, description.data())))
        {
            wprintf(L"Target adapter: %hs (%llu MiB dedicated, integrated=%s)\n",
                    description.data(), bestDedicatedMemory / (1024ull * 1024ull),
                    bestIsIntegrated ? L"yes" : L"no");
        }
    }

    *outAdapter = bestAdapter.Detach();
    return S_OK;
}

inline HRESULT CreateHardwareExecutionTarget(_In_ IWinMLRuntime* runtime, const DeviceArgs& device,
                                             _COM_Outptr_ IWinMLExecutionTarget** target) noexcept
{
    if (runtime == nullptr || target == nullptr)
    {
        return E_POINTER;
    }

    *target = nullptr;

    if (device.deviceType == WINML_EXECUTION_TARGET_KIND_CPU)
    {
        return runtime->CreateCpuExecutionTarget(target);
    }

    const GUID& hardwareAttribute = (device.deviceType == WINML_EXECUTION_TARGET_KIND_NPU)
                                        ? DXCORE_HARDWARE_TYPE_ATTRIBUTE_NPU
                                        : DXCORE_ADAPTER_ATTRIBUTE_D3D12_GRAPHICS;

    ComPtr<IDXCoreAdapter> adapter;
    std::vector<EpHardwareId> allowedHardwareIds;

    // Restrict adapter selection to devices reported by the pinned provider.
    // Without this, memory-based ranking can choose an adapter the provider
    // cannot use.
    if (!device.epName.empty())
    {
        const char* providerName = ResolveExecutionProviderName(device.epName.c_str());
        if (!IsWebGpuEpName(providerName))
        {
            TryGetEpHardwareIds(providerName, HardwareTypeForDeviceClass(device.deviceType),
                                allowedHardwareIds);
        }
    }

    CHECK_HR(FindHardwareAdapter(hardwareAttribute, device.executionPolicy, allowedHardwareIds,
                                 adapter.GetAddressOf()));
    return runtime->CreateExecutionTargetFromAdapter(adapter.Get(), target);
}

inline HRESULT CreateExecutionTarget(_In_ IWinMLRuntime* runtime, const DeviceArgs& device,
                                     _COM_Outptr_ IWinMLExecutionTarget** target,
                                     _COM_Outptr_opt_ IWinMLExecutionTarget** tensorTarget) noexcept
{
    if (runtime == nullptr || target == nullptr)
    {
        return E_POINTER;
    }

    *target = nullptr;
    if (tensorTarget != nullptr)
    {
        *tensorTarget = nullptr;
    }

    // First create the concrete hardware target. If the stage is provider-pinned,
    // the same hardware target is passed into IWinMLOrtCompatibility below.
    ComPtr<IWinMLExecutionTarget> hardwareTarget;
    CHECK_HR(CreateHardwareExecutionTarget(runtime, device, hardwareTarget.GetAddressOf()));
    if (tensorTarget != nullptr)
    {
        *tensorTarget = hardwareTarget.Get();
        (*tensorTarget)->AddRef();
    }

    const char* providerName =
        device.epName.empty() ? nullptr : ResolveExecutionProviderName(device.epName.c_str());
    if (providerName == nullptr || _stricmp(providerName, "WebGpuExecutionProvider") == 0)
    {
        if (providerName != nullptr)
        {
            wprintf(L"WebGPU uses the Runtime-owned default GPU target.\n");
        }

        *target = hardwareTarget.Detach();
        return S_OK;
    }

    // Provider-pinned stages use IWinMLOrtCompatibility; target creation fails
    // if the provider and requested device class cannot be combined.
    const std::wstring providerNameWide = Utf8ToWide(providerName);

    ComPtr<IWinMLOrtCompatibility> compatibility;
    CHECK_HR(runtime->QueryInterface(IID_PPV_ARGS(compatibility.GetAddressOf())));
    CHECK_HR(compatibility->CreateExecutionTarget(providerNameWide.c_str(), device.deviceType,
                                                  hardwareTarget.Get(), target));

    wprintf(L"Pinned execution provider: %s (%s)\n", providerNameWide.c_str(),
            DeviceTypeName(device.deviceType));
    return S_OK;
}

inline HRESULT CreateExecutionTarget(_In_ IWinMLRuntime* runtime, const DeviceArgs& device,
                                     _COM_Outptr_ IWinMLExecutionTarget** target) noexcept
{
    return CreateExecutionTarget(runtime, device, target, nullptr);
}

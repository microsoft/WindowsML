// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Execution-target creation shared by the C# samples, matching
// execution_target_utils.h in the C++ samples.
//
// CPU requests use the Runtime's CPU target. GPU and NPU requests rank the
// DXCore adapters of that class, by dedicated memory or, with --efficiency, by
// preferring integrated adapters, and create a target for the best one. When
// --ep pins a provider, ranking is limited to the adapters that provider
// reports, and IWinMLOrtCompatibility pairs the provider with that hardware
// target.

using System.Runtime.InteropServices;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Windows.Win32.Graphics.DXCore;
using Windows.Win32.System.Com;
using Win32 = Windows.Win32.PInvoke;

namespace WinMLSamples;

internal static unsafe class ExecutionTargets
{
    // Registers any provider --ep requests, then creates the stage target.
    public static IWinMLExecutionTarget* PrepareAndCreate(IWinMLRuntime* runtime, DeviceOptions device, ComObjects objects)
    {
        ExecutionProviders.Prepare(device);
        return Create(runtime, device, objects);
    }

    public static IWinMLExecutionTarget* Create(IWinMLRuntime* runtime, DeviceOptions device, ComObjects objects)
    {
        IWinMLExecutionTarget* hardwareTarget = CreateHardwareTarget(runtime, device, objects);
        if (string.IsNullOrEmpty(device.Ep))
        {
            return hardwareTarget;
        }

        string providerName = ExecutionProviders.ResolveName(device.Ep);
        if (ExecutionProviders.IsWebGpu(providerName))
        {
            Console.WriteLine("WebGPU uses the Runtime-owned default GPU target.");
            return hardwareTarget;
        }

        // A provider-pinned target fails to initialize if the provider cannot
        // run on the requested device class.
        IWinMLOrtCompatibility* compatibility = objects.QueryInterface<IWinMLOrtCompatibility>(runtime);
        IWinMLExecutionTarget* target = objects.Add(compatibility->CreateExecutionTarget(providerName, device.Kind, hardwareTarget));
        Console.WriteLine($"Pinned execution provider: {providerName} ({DeviceOptions.KindName(device.Kind)})");
        return target;
    }

    private static IWinMLExecutionTarget* CreateHardwareTarget(IWinMLRuntime* runtime, DeviceOptions device, ComObjects objects)
    {
        if (device.Kind == WINML_EXECUTION_TARGET_KIND.WINML_EXECUTION_TARGET_KIND_CPU)
        {
            return objects.Add(runtime->CreateCpuExecutionTarget());
        }

        IReadOnlyList<(uint VendorId, uint DeviceId)> allowed = Array.Empty<(uint, uint)>();
        if (!string.IsNullOrEmpty(device.Ep) && !ExecutionProviders.IsWebGpu(device.Ep))
        {
            allowed = ExecutionProviders.GetHardwareIds(ExecutionProviders.ResolveName(device.Ep), device.Kind);
        }

        Guid attribute = device.Kind == WINML_EXECUTION_TARGET_KIND.WINML_EXECUTION_TARGET_KIND_NPU
            ? Win32.DXCORE_HARDWARE_TYPE_ATTRIBUTE_NPU
            : Win32.DXCORE_ADAPTER_ATTRIBUTE_D3D12_GRAPHICS;
        IDXCoreAdapter* adapter = FindAdapter(attribute, device.Policy, allowed, objects);
        return objects.Add(runtime->CreateExecutionTargetFromAdapter(ComObjects.AsUnknownArgument(adapter)));
    }

    private static IDXCoreAdapter* FindAdapter(
        Guid attribute,
        ExecutionPolicy policy,
        IReadOnlyList<(uint VendorId, uint DeviceId)> allowed,
        ComObjects objects)
    {
        IDXCoreAdapterFactory* factory;
        Guid factoryIid = typeof(IDXCoreAdapterFactory).GUID;
        Win32.DXCoreCreateAdapterFactory(&factoryIid, (void**)&factory).ThrowOnFailure();
        objects.Add(factory);

        IDXCoreAdapterList* list;
        Guid listIid = typeof(IDXCoreAdapterList).GUID;
        factory->CreateAdapterList(1, &attribute, &listIid, (void**)&list);
        objects.Add(list);

        IDXCoreAdapter* best = null;
        ulong bestMemory = 0;
        bool bestIntegrated = false;
        bool excludedByProvider = false;
        Guid adapterIid = typeof(IDXCoreAdapter).GUID;
        uint count = list->GetAdapterCount();
        for (uint i = 0; i < count; ++i)
        {
            IDXCoreAdapter* adapter;
            if (list->GetAdapter(i, &adapterIid, (void**)&adapter).Failed)
            {
                continue;
            }

            objects.Add(adapter);
            if (!GetProperty<bool>(adapter, DXCoreAdapterProperty.IsHardware))
            {
                continue;
            }

            if (allowed.Count > 0)
            {
                if (!TryGetProperty(adapter, DXCoreAdapterProperty.HardwareID, out DXCoreHardwareID id))
                {
                    continue;
                }

                if (!allowed.Contains((id.vendorID, id.deviceID)))
                {
                    excludedByProvider = true;
                    continue;
                }
            }

            ulong memory = GetProperty<ulong>(adapter, DXCoreAdapterProperty.DedicatedAdapterMemory);
            bool integrated = GetProperty<bool>(adapter, DXCoreAdapterProperty.IsIntegrated);
            bool better = best == null ||
                (policy == ExecutionPolicy.PreferEfficiency
                    ? (integrated && !bestIntegrated) || (integrated == bestIntegrated && memory < bestMemory)
                    : memory > bestMemory);
            if (better)
            {
                best = adapter;
                bestMemory = memory;
                bestIntegrated = integrated;
            }
        }

        if (best == null)
        {
            // HRESULT_FROM_WIN32(ERROR_NOT_FOUND), as the C++ helper returns.
            throw new COMException(
                excludedByProvider
                    ? "The pinned execution provider exposes no adapter that this device class can use."
                    : "No adapter of the requested device class is available. Run with --device cpu.",
                unchecked((int)0x80070490));
        }

        Console.WriteLine(
            $"Target adapter: {GetDescription(best)} ({bestMemory / (1024 * 1024)} MiB dedicated, integrated={(bestIntegrated ? "yes" : "no")})");
        return best;
    }

    // Returns the default value when the adapter does not report the property.
    private static T GetProperty<T>(IDXCoreAdapter* adapter, DXCoreAdapterProperty property)
        where T : unmanaged
    {
        return TryGetProperty(adapter, property, out T value) ? value : default;
    }

    private static bool TryGetProperty<T>(IDXCoreAdapter* adapter, DXCoreAdapterProperty property, out T value)
        where T : unmanaged
    {
        T result = default;
        bool succeeded = adapter->GetProperty(property, (nuint)sizeof(T), &result).Succeeded;
        value = result;
        return succeeded;
    }

    private static string GetDescription(IDXCoreAdapter* adapter)
    {
        nuint size;
        if (adapter->GetPropertySize(DXCoreAdapterProperty.DriverDescription, &size).Failed || size <= 1)
        {
            return "(unnamed adapter)";
        }

        byte[] buffer = new byte[size];
        fixed (byte* data = buffer)
        {
            if (adapter->GetProperty(DXCoreAdapterProperty.DriverDescription, size, data).Failed)
            {
                return "(unnamed adapter)";
            }
        }

        return System.Text.Encoding.UTF8.GetString(buffer, 0, (int)size - 1);
    }
}

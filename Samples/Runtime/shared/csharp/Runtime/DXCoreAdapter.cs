// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. DXCore adapter enumeration and ranking,
// for creating an execution target on a specific GPU or NPU.

using Windows.Win32.Graphics.DXCore;
using Win32 = Windows.Win32.PInvoke;

namespace WinMLSamples.Runtime;

public enum DXCoreAdapterKind
{
    // Adapters that support Direct3D 12 graphics: GPUs.
    Gpu,

    // Neural processing units.
    Npu,
}

// The PCI identity of an adapter.
public readonly record struct DXCoreHardwareId(uint VendorId, uint DeviceId, uint SubsystemId, uint Revision);

public sealed unsafe class DXCoreAdapter : WinMLObject
{
    private DXCoreAdapter(IDXCoreAdapter* pointer)
        : base(pointer)
    {
    }

    // The driver description, or an empty string when the adapter reports none.
    public string Description
    {
        get
        {
            nuint size = 0;
            if (Self->GetPropertySize(DXCoreAdapterProperty.DriverDescription, &size).Failed || size <= 1)
            {
                GC.KeepAlive(this);
                return string.Empty;
            }

            byte[] buffer = new byte[size];
            bool succeeded;
            fixed (byte* data = buffer)
            {
                succeeded = Self->GetProperty(DXCoreAdapterProperty.DriverDescription, size, data).Succeeded;
            }

            GC.KeepAlive(this);
            return succeeded ? System.Text.Encoding.UTF8.GetString(buffer, 0, (int)size - 1) : string.Empty;
        }
    }

    // The PCI identity, or null when the adapter reports none.
    public DXCoreHardwareId? HardwareId
    {
        get
        {
            return TryGetProperty(DXCoreAdapterProperty.HardwareID, out DXCoreHardwareID id)
                ? new DXCoreHardwareId(id.vendorID, id.deviceID, id.subSysID, id.revision)
                : null;
        }
    }

    public ulong DedicatedAdapterMemory => GetProperty<ulong>(DXCoreAdapterProperty.DedicatedAdapterMemory);

    // False for software adapters such as the Microsoft Basic Render Driver.
    public bool IsHardware => GetProperty<byte>(DXCoreAdapterProperty.IsHardware) != 0;

    // True for adapters that share system memory with the CPU.
    public bool IsIntegrated => GetProperty<byte>(DXCoreAdapterProperty.IsIntegrated) != 0;

    private IDXCoreAdapter* Self => (IDXCoreAdapter*)ThisPtr;

    // Lists the adapters of a class in DXCore order. Dispose each one.
    public static IReadOnlyList<DXCoreAdapter> GetAdapters(DXCoreAdapterKind kind)
    {
        Guid attribute = kind switch
        {
            DXCoreAdapterKind.Gpu => Win32.DXCORE_ADAPTER_ATTRIBUTE_D3D12_GRAPHICS,
            DXCoreAdapterKind.Npu => Win32.DXCORE_HARDWARE_TYPE_ATTRIBUTE_NPU,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        IDXCoreAdapterFactory* factory = null;
        IDXCoreAdapterList* list = null;
        try
        {
            Guid factoryIid = IDXCoreAdapterFactory.IID_Guid;
            WinMLException.ThrowIfFailed(Win32.DXCoreCreateAdapterFactory(&factoryIid, (void**)&factory));

            Guid listIid = IDXCoreAdapterList.IID_Guid;
            WinMLException.ThrowIfFailed(factory->CreateAdapterList(1, &attribute, &listIid, (void**)&list));

            uint count = list->GetAdapterCount();
            List<DXCoreAdapter> adapters = new((int)count);
            Guid adapterIid = IDXCoreAdapter.IID_Guid;
            for (uint i = 0; i < count; ++i)
            {
                IDXCoreAdapter* adapter = null;
                if (list->GetAdapter(i, &adapterIid, (void**)&adapter).Succeeded && adapter != null)
                {
                    adapters.Add(new DXCoreAdapter(adapter));
                }
            }

            return adapters;
        }
        finally
        {
            Release(list);
            Release(factory);
        }
    }

    // Returns the hardware adapter of a class that best matches a preference:
    // the most dedicated memory, or with Efficiency, integrated adapters first
    // and then the least dedicated memory. A filter can limit the candidates,
    // for example to the devices an execution provider supports. Returns null
    // when no hardware adapter passes.
    public static DXCoreAdapter? FindBest(
        DXCoreAdapterKind kind,
        ExecutionTargetPreference preference = ExecutionTargetPreference.Default,
        Func<DXCoreAdapter, bool>? filter = null)
    {
        DXCoreAdapter? best = null;
        ulong bestMemory = 0;
        bool bestIntegrated = false;
        foreach (DXCoreAdapter adapter in GetAdapters(kind))
        {
            if (!adapter.IsHardware || (filter is not null && !filter(adapter)))
            {
                adapter.Dispose();
                continue;
            }

            ulong memory = adapter.DedicatedAdapterMemory;
            bool integrated = adapter.IsIntegrated;
            bool better = best is null ||
                (preference == ExecutionTargetPreference.Efficiency
                    ? (integrated && !bestIntegrated) || (integrated == bestIntegrated && memory < bestMemory)
                    : memory > bestMemory);
            if (better)
            {
                best?.Dispose();
                best = adapter;
                bestMemory = memory;
                bestIntegrated = integrated;
            }
            else
            {
                adapter.Dispose();
            }
        }

        return best;
    }

    // Returns the default value when the adapter does not report the property.
    private T GetProperty<T>(DXCoreAdapterProperty property)
        where T : unmanaged
    {
        return TryGetProperty(property, out T value) ? value : default;
    }

    private bool TryGetProperty<T>(DXCoreAdapterProperty property, out T value)
        where T : unmanaged
    {
        T result = default;
        bool succeeded = Self->GetProperty(property, (nuint)sizeof(T), &result).Succeeded;
        GC.KeepAlive(this);
        value = result;
        return succeeded;
    }
}

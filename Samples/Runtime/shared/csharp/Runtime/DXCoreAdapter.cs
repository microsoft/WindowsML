// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. DXCore adapter enumeration and ranking,
// for creating an execution target on a specific GPU or NPU.

using Windows.Win32.Foundation;
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

// An adapter's locally unique identifier. DXCore adapters and Direct3D 12
// devices on the same adapter report the same value. It is unique on this
// machine until the system restarts, and an adapter can get a new one, for
// example when its driver restarts.
public readonly record struct AdapterLuid(uint LowPart, int HighPart);

// The memory that DXCoreAdapter.QueryMemoryBudget reports on. Local is the
// memory the adapter treats as local and fastest: dedicated memory on a
// discrete GPU, system memory on an integrated one. NonLocal is slower memory
// the adapter reaches, such as system memory for a discrete GPU.
public enum DXCoreMemorySegmentGroup
{
    Local = 0,
    NonLocal = 1,
}

// This process's video memory budget and use in one segment group of an adapter, in bytes.
public readonly record struct DXCoreMemoryBudget(ulong Budget, ulong CurrentUsage, ulong AvailableForReservation, ulong CurrentReservation);

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

    public AdapterLuid InstanceLuid
    {
        get
        {
            LUID luid;
            Check(Self->GetProperty(DXCoreAdapterProperty.InstanceLuid, (nuint)sizeof(LUID), &luid));
            return new AdapterLuid(luid.LowPart, luid.HighPart);
        }
    }

    private IDXCoreAdapter* Self => (IDXCoreAdapter*)ThisPtr;

    // This process's video memory budget and use on the adapter. The usage
    // covers every device the process created on the adapter, including the
    // Runtime's own.
    public DXCoreMemoryBudget QueryMemoryBudget(DXCoreMemorySegmentGroup segmentGroup = DXCoreMemorySegmentGroup.Local, uint nodeIndex = 0)
    {
        DXCoreAdapterMemoryBudgetNodeSegmentGroup input = new() { nodeIndex = nodeIndex, segmentGroup = (DXCoreSegmentGroup)segmentGroup };
        DXCoreAdapterMemoryBudget budget;
        Check(Self->QueryState(
            DXCoreAdapterState.AdapterMemoryBudget,
            (nuint)sizeof(DXCoreAdapterMemoryBudgetNodeSegmentGroup),
            &input,
            (nuint)sizeof(DXCoreAdapterMemoryBudget),
            &budget));
        return new DXCoreMemoryBudget(budget.budget, budget.currentUsage, budget.availableForReservation, budget.currentReservation);
    }

    // Finds an adapter by its LUID, such as the one a GPU execution target's
    // Direct3D 12 device reports (D3D12ExecutionTarget.GetAdapterLuid). Dispose it.
    public static DXCoreAdapter GetAdapterByLuid(AdapterLuid luid)
    {
        IDXCoreAdapterFactory* factory = CreateFactory();
        try
        {
            LUID native = new() { LowPart = luid.LowPart, HighPart = luid.HighPart };
            IDXCoreAdapter* adapter = null;
            Guid adapterIid = IDXCoreAdapter.IID_Guid;
            WinMLException.ThrowIfFailed(factory->GetAdapterByLuid(&native, &adapterIid, (void**)&adapter));
            return new DXCoreAdapter(Required(adapter));
        }
        finally
        {
            Release(factory);
        }
    }

    // Lists the adapters of a class in DXCore order. Dispose each one.
    public static IReadOnlyList<DXCoreAdapter> GetAdapters(DXCoreAdapterKind kind)
    {
        Guid attribute = kind switch
        {
            DXCoreAdapterKind.Gpu => Win32.DXCORE_ADAPTER_ATTRIBUTE_D3D12_GRAPHICS,
            DXCoreAdapterKind.Npu => Win32.DXCORE_HARDWARE_TYPE_ATTRIBUTE_NPU,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        IDXCoreAdapterFactory* factory = CreateFactory();
        IDXCoreAdapterList* list = null;
        try
        {
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
        IReadOnlyList<DXCoreAdapter> adapters = GetAdapters(kind);
        DXCoreAdapter? result = null;
        try
        {
            DXCoreAdapter? best = null;
            ulong bestMemory = 0;
            bool bestIntegrated = false;
            foreach (DXCoreAdapter adapter in adapters)
            {
                if (!adapter.IsHardware || (filter is not null && !filter(adapter)))
                {
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
                    best = adapter;
                    bestMemory = memory;
                    bestIntegrated = integrated;
                }
            }

            result = best;
        }
        finally
        {
            // Release every adapter but the result, including when the filter throws.
            foreach (DXCoreAdapter adapter in adapters)
            {
                if (!ReferenceEquals(adapter, result))
                {
                    adapter.Dispose();
                }
            }
        }

        return result;
    }

    private static IDXCoreAdapterFactory* CreateFactory()
    {
        IDXCoreAdapterFactory* factory = null;
        Guid factoryIid = IDXCoreAdapterFactory.IID_Guid;
        WinMLException.ThrowIfFailed(Win32.DXCoreCreateAdapterFactory(&factoryIid, (void**)&factory));
        return Required(factory);
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

// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.DXCore;
using Windows.Win32.System.Com;
using Native = Windows.Win32.PInvoke;

namespace TaskStudio.Runtime;

public sealed record GpuDevice(string Name, uint VendorId, uint LuidLow, int LuidHigh)
{
    public bool IsNvidia => VendorId == 0x10DE;
    public string Identity => $"{LuidHigh:X8}:{LuidLow:X8}";
}

internal sealed partial class TaskRuntime
{
    public unsafe Task<IReadOnlyList<GpuDevice>> GetGpuDevicesAsync() => Run<IReadOnlyList<GpuDevice>>(() =>
    {
        IDXCoreAdapterFactory* factory = CreateAdapterFactory();
        IDXCoreAdapterList* adapters = null;
        try
        {
            Guid attribute = Native.DXCORE_ADAPTER_ATTRIBUTE_D3D12_GRAPHICS;
            Guid iid = typeof(IDXCoreAdapterList).GUID;
            factory->CreateAdapterList(1, &attribute, &iid, (void**)&adapters);
            var devices = new List<GpuDevice>();
            for (uint index = 0; index < adapters->GetAdapterCount(); index++)
            {
                IDXCoreAdapter* adapter = null;
                try
                {
                    iid = typeof(IDXCoreAdapter).GUID;
                    adapters->GetAdapter(index, &iid, (void**)&adapter);
                    byte hardware;
                    adapter->GetProperty(DXCoreAdapterProperty.IsHardware, sizeof(byte), &hardware);
                    if (hardware != 0) devices.Add(DescribeAdapter(adapter));
                }
                finally { Release(adapter); }
            }
            return devices.OrderByDescending(device => device.IsNvidia).ThenBy(device => device.Name).ToArray();
        }
        finally { Release(adapters); Release(factory); }
    });

    // This process's local (dedicated) video memory on the adapter, as DXCore reports it.
    public unsafe Task<ulong> GetGpuMemoryUsageAsync(GpuDevice selection) => Run(() =>
    {
        IDXCoreAdapterFactory* factory = CreateAdapterFactory();
        IDXCoreAdapter* adapter = null;
        try
        {
            var luid = new LUID { LowPart = selection.LuidLow, HighPart = selection.LuidHigh };
            Guid iid = typeof(IDXCoreAdapter).GUID;
            factory->GetAdapterByLuid(&luid, &iid, (void**)&adapter);
            var input = new DXCoreAdapterMemoryBudgetNodeSegmentGroup { nodeIndex = 0, segmentGroup = DXCoreSegmentGroup.Local };
            DXCoreAdapterMemoryBudget budget;
            adapter->QueryState(DXCoreAdapterState.AdapterMemoryBudget, (nuint)sizeof(DXCoreAdapterMemoryBudgetNodeSegmentGroup), &input,
                (nuint)sizeof(DXCoreAdapterMemoryBudget), &budget);
            return budget.currentUsage;
        }
        finally { Release(adapter); Release(factory); }
    });

    private static unsafe IDXCoreAdapterFactory* CreateAdapterFactory()
    {
        IDXCoreAdapterFactory* factory = null;
        Guid iid = typeof(IDXCoreAdapterFactory).GUID;
        Native.DXCoreCreateAdapterFactory(&iid, (void**)&factory).ThrowOnFailure();
        return factory;
    }

    private static unsafe GpuDevice DescribeAdapter(IDXCoreAdapter* adapter)
    {
        DXCoreHardwareID hardware;
        LUID luid;
        nuint size;
        adapter->GetProperty(DXCoreAdapterProperty.HardwareID, (nuint)sizeof(DXCoreHardwareID), &hardware);
        adapter->GetProperty(DXCoreAdapterProperty.InstanceLuid, (nuint)sizeof(LUID), &luid);
        adapter->GetPropertySize(DXCoreAdapterProperty.DriverDescription, &size);
        var description = new byte[checked((int)size)];
        fixed (byte* bytes = description)
            adapter->GetProperty(DXCoreAdapterProperty.DriverDescription, size, bytes);
        string name = Encoding.UTF8.GetString(description).TrimEnd('\0');
        return new(name, hardware.vendorID, luid.LowPart, luid.HighPart);
    }

    private static unsafe IWinMLExecutionTarget* CreateGpuTarget(IWinMLRuntime* owner, GpuDevice selection)
    {
        IDXCoreAdapterFactory* factory = CreateAdapterFactory();
        IDXCoreAdapter* adapter = null;
        try
        {
            var luid = new LUID { LowPart = selection.LuidLow, HighPart = selection.LuidHigh };
            Guid iid = typeof(IDXCoreAdapter).GUID;
            factory->GetAdapterByLuid(&luid, &iid, (void**)&adapter);
            if (DescribeAdapter(adapter).VendorId != selection.VendorId)
                throw new InvalidOperationException("The selected GPU changed. Restart the app to refresh its device list.");
            // Let the Runtime create its own D3D12/Agility device on this exact adapter.
            return owner->CreateExecutionTargetFromAdapter((IUnknown*)adapter);
        }
        finally { Release(adapter); Release(factory); }
    }

    private static unsafe GpuDevice DescribeGpuTarget(IWinMLExecutionTarget* target)
    {
        if (target->GetKind() != WINML_EXECUTION_TARGET_KIND.WINML_EXECUTION_TARGET_KIND_GPU)
            throw new InvalidOperationException("The Runtime did not create a GPU target. CPU fallback is not enabled.");
        IWinMLD3D12ExecutionTarget* graphics = null;
        IUnknown* unknownDevice = null;
        ID3D12Device* device = null;
        IDXCoreAdapterFactory* factory = null;
        IDXCoreAdapter* adapter = null;
        try
        {
            target->QueryInterface(out graphics).ThrowOnFailure();
            unknownDevice = graphics->GetDevice();
            unknownDevice->QueryInterface(out device).ThrowOnFailure();
            LUID luid = device->GetAdapterLuid();
            factory = CreateAdapterFactory();
            Guid iid = typeof(IDXCoreAdapter).GUID;
            factory->GetAdapterByLuid(&luid, &iid, (void**)&adapter);
            return DescribeAdapter(adapter);
        }
        finally { Release(adapter); Release(factory); Release(device); Release(unknownDevice); Release(graphics); }
    }
}

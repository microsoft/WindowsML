// Copyright (C) Microsoft Corporation. All rights reserved.

using WinMLSamples.Runtime;

namespace TaskStudio.Runtime;

public sealed record GpuDevice(string Name, uint VendorId, uint LuidLow, int LuidHigh)
{
    public bool IsNvidia => VendorId == 0x10DE;
    public string Identity => $"{LuidHigh:X8}:{LuidLow:X8}";
}

internal sealed partial class TaskRuntime
{
    public Task<IReadOnlyList<GpuDevice>> GetGpuDevicesAsync() => Run<IReadOnlyList<GpuDevice>>(() =>
    {
        IReadOnlyList<DXCoreAdapter> adapters = DXCoreAdapter.GetAdapters(DXCoreAdapterKind.Gpu);
        try
        {
            return adapters.Where(adapter => adapter.IsHardware).Select(Describe)
                .OrderByDescending(device => device.IsNvidia).ThenBy(device => device.Name).ToArray();
        }
        finally
        {
            foreach (DXCoreAdapter adapter in adapters) adapter.Dispose();
        }
    });

    // This process's use of the adapter's local memory, as DXCore reports it: dedicated memory on a discrete GPU,
    // shared system memory on an integrated one.
    public Task<ulong> GetGpuMemoryUsageAsync(GpuDevice selection) => Run(() =>
    {
        using DXCoreAdapter adapter = DXCoreAdapter.GetAdapterByLuid(LuidOf(selection));
        return adapter.QueryMemoryBudget(DXCoreMemorySegmentGroup.Local).CurrentUsage;
    });

    private static AdapterLuid LuidOf(GpuDevice device) => new(device.LuidLow, device.LuidHigh);

    private static GpuDevice Describe(DXCoreAdapter adapter)
    {
        AdapterLuid luid = adapter.InstanceLuid;
        return new(adapter.Description, adapter.HardwareId?.VendorId ?? 0, luid.LowPart, luid.HighPart);
    }

    private static ExecutionTarget CreateGpuTarget(WinMLRuntime owner, GpuDevice selection)
    {
        using DXCoreAdapter adapter = DXCoreAdapter.GetAdapterByLuid(LuidOf(selection));
        if ((adapter.HardwareId?.VendorId ?? 0) != selection.VendorId)
            throw new InvalidOperationException("The selected GPU changed. Restart the app to refresh its device list.");
        // Let the Runtime create its own D3D12/Agility device on this exact adapter.
        return owner.CreateExecutionTarget(adapter);
    }

    private static GpuDevice DescribeGpuTarget(ExecutionTarget target)
    {
        if (target.Kind != ExecutionTargetKind.Gpu)
            throw new InvalidOperationException("The Runtime did not create a GPU target. CPU fallback is not enabled.");
        using D3D12ExecutionTarget graphics = target.As<D3D12ExecutionTarget>();
        using DXCoreAdapter adapter = DXCoreAdapter.GetAdapterByLuid(graphics.GetAdapterLuid());
        return Describe(adapter);
    }
}

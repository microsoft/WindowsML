// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Execution-target creation shared by the C# samples, matching
// execution_target_utils.h in the C++ samples.
//
// CPU requests use the Runtime's CPU target. GPU and NPU requests pick the
// DXCore hardware adapter of that class with the most dedicated memory or,
// with --efficiency, an integrated adapter, and create a target for it. When
// --ep pins a provider, only the adapters that provider reports are
// considered, and the Runtime pairs the provider with that hardware target.

using WinMLSamples.Runtime;

namespace WinMLSamples;

internal static class ExecutionTargets
{
    // Registers any provider --ep requests, then creates the stage target.
    public static ExecutionTarget PrepareAndCreate(WinMLRuntime runtime, DeviceOptions device)
    {
        ExecutionProviders.Prepare(device);
        return Create(runtime, device);
    }

    public static ExecutionTarget Create(WinMLRuntime runtime, DeviceOptions device)
    {
        if (string.IsNullOrEmpty(device.Ep))
        {
            return CreateHardwareTarget(runtime, device);
        }

        string providerName = ExecutionProviders.ResolveName(device.Ep);

        // A provider-pinned target fails to initialize if the provider cannot
        // run on the requested device class.
        using ExecutionTarget hardwareTarget = CreateHardwareTarget(runtime, device);
        ExecutionTarget target = runtime.CreateExecutionTarget(providerName, device.Kind, hardwareTarget);
        Console.WriteLine($"Pinned execution provider: {providerName} ({DeviceOptions.KindName(device.Kind)})");
        return target;
    }

    private static ExecutionTarget CreateHardwareTarget(WinMLRuntime runtime, DeviceOptions device)
    {
        if (device.Kind == ExecutionTargetKind.Cpu)
        {
            return runtime.CreateCpuExecutionTarget();
        }

        IReadOnlyList<(uint VendorId, uint DeviceId)> allowed = Array.Empty<(uint, uint)>();
        if (!string.IsNullOrEmpty(device.Ep) && !ExecutionProviders.IsWebGpu(device.Ep))
        {
            allowed = ExecutionProviders.GetHardwareIds(ExecutionProviders.ResolveName(device.Ep), device.Kind);
        }

        DXCoreAdapterKind adapterKind = device.Kind == ExecutionTargetKind.Npu ? DXCoreAdapterKind.Npu : DXCoreAdapterKind.Gpu;
        using DXCoreAdapter adapter = DXCoreAdapter.FindBest(
            adapterKind,
            device.Preference,
            allowed.Count == 0 ? null : candidate => IsAllowed(candidate, allowed))
            ?? throw NoAdapter(adapterKind, device.Preference, allowed);

        Console.WriteLine(
            $"Target adapter: {Describe(adapter)} ({adapter.DedicatedAdapterMemory / (1024 * 1024)} MiB dedicated, " +
            $"integrated={(adapter.IsIntegrated ? "yes" : "no")})");
        return runtime.CreateExecutionTarget(adapter);
    }

    // Provider devices are identified by PCI vendor and device ID.
    private static bool IsAllowed(DXCoreAdapter adapter, IReadOnlyList<(uint VendorId, uint DeviceId)> allowed)
    {
        return adapter.HardwareId is DXCoreHardwareId id && allowed.Contains((id.VendorId, id.DeviceId));
    }

    // The same HRESULT_FROM_WIN32(ERROR_NOT_FOUND) as the C++ helper.
    private static WinMLException NoAdapter(
        DXCoreAdapterKind kind,
        ExecutionTargetPreference preference,
        IReadOnlyList<(uint VendorId, uint DeviceId)> allowed)
    {
        bool excludedByProvider = false;
        if (allowed.Count > 0)
        {
            using DXCoreAdapter? any = DXCoreAdapter.FindBest(kind, preference);
            excludedByProvider = any is not null;
        }

        return new WinMLException(
            excludedByProvider
                ? "The pinned execution provider exposes no adapter that this device class can use."
                : "No adapter of the requested device class is available. Run with --device cpu.",
            WinMLException.NotFound);
    }

    private static string Describe(DXCoreAdapter adapter)
    {
        string description = adapter.Description;
        return description.Length == 0 ? "(unnamed adapter)" : description;
    }
}

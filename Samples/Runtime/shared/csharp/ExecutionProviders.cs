// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Execution-provider setup shared by the C# samples, matching
// ep_catalog_utils.h in the C++ samples.
//
// A run without --ep uses the Runtime default. An explicit --ep finds the
// provider in the Windows ML execution-provider catalog, makes it ready, and
// registers its library with ONNX Runtime, so the Runtime can create a target
// pinned to it. The catalog is the Microsoft.Windows.AI.MachineLearning WinRT
// API and registration uses the ONNX Runtime C# API; both assemblies come with
// the Microsoft.Windows.AI.MachineLearning package and share the process's
// ONNX Runtime environment with the Runtime.

using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;
using Microsoft.Windows.AI.MachineLearning.Runtime;

namespace WinMLSamples;

internal static class ExecutionProviders
{
    public const string CpuProviderName = "CPUExecutionProvider";
    public const string WebGpuProviderName = "WebGpuExecutionProvider";

    private static readonly Dictionary<string, string> s_aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["webgpu"] = WebGpuProviderName,
        ["nvtensorrtrtx"] = "NvTensorRTRTXExecutionProvider",
        ["trtrtx"] = "NvTensorRTRTXExecutionProvider",
        ["openvino"] = "OpenVINOExecutionProvider",
        ["qnn"] = "QNNExecutionProvider",
        ["vitisai"] = "VitisAIExecutionProvider",
        ["migraphx"] = "MIGraphXExecutionProvider",
    };

    // Maps a short alias to the provider's registered name.
    public static string ResolveName(string name)
    {
        return s_aliases.TryGetValue(name, out string? fullName) ? fullName : name;
    }

    public static bool IsWebGpu(string? name)
    {
        return name is not null && string.Equals(ResolveName(name), WebGpuProviderName, StringComparison.OrdinalIgnoreCase);
    }

    // Creates the process's ONNX Runtime environment with verbose logging, so
    // node-placement lines show which provider ran each node. Call before the
    // Runtime is created; the logging level of an existing environment does
    // not change.
    public static void EnableVerboseLogging()
    {
        EnvironmentCreationOptions options = new()
        {
            logId = "WinMLSample",
            logLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE,
        };
        OrtEnv.CreateInstanceWithOptions(ref options);
        Console.WriteLine("  Verbose logging enabled for inference node placement.");
    }

    // Prepares the provider that --ep requests and checks that it exposes the
    // requested device class. --device npu requires --ep, because there is no
    // provider-neutral NPU fallback.
    public static void Prepare(DeviceOptions device)
    {
        bool isNpu = device.Kind == WINML_EXECUTION_TARGET_KIND.WINML_EXECUTION_TARGET_KIND_NPU;
        if (string.IsNullOrEmpty(device.Ep))
        {
            if (isNpu)
            {
                throw new ArgumentException("--device npu requires --ep <provider>.");
            }

            return;
        }

        string providerName = ResolveName(device.Ep);
        if (string.Equals(providerName, CpuProviderName, StringComparison.OrdinalIgnoreCase))
        {
            if (device.Kind != WINML_EXECUTION_TARGET_KIND.WINML_EXECUTION_TARGET_KIND_CPU)
            {
                throw new ArgumentException($"{CpuProviderName} supports CPU execution only.");
            }

            Console.WriteLine($"  {CpuProviderName} is built in; no catalog registration is required.");
            return;
        }

        if (IsWebGpu(providerName))
        {
            if (device.Kind != WINML_EXECUTION_TARGET_KIND.WINML_EXECUTION_TARGET_KIND_GPU)
            {
                throw new ArgumentException("WebGPU supports GPU execution only.");
            }

            Console.WriteLine("  WebGPU: the Runtime acquires and registers it for the pinned GPU target.");
            return;
        }

        RegisterFromCatalog(device.Ep, providerName, device.Kind);
        if (!ReportDevices(providerName, HardwareType(device.Kind)))
        {
            // Same HRESULT as the C++ helper: HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED).
            throw new COMException(
                $"Provider '{providerName}' does not expose the requested {DeviceOptions.KindName(device.Kind)} device.",
                unchecked((int)0x80070032));
        }
    }

    // The PCI vendor and device IDs of the devices of one class that a
    // registered provider exposes. Adapter selection stays within this set.
    public static IReadOnlyList<(uint VendorId, uint DeviceId)> GetHardwareIds(string providerName, WINML_EXECUTION_TARGET_KIND kind)
    {
        OrtHardwareDeviceType type = HardwareType(kind);
        List<(uint, uint)> ids = new();
        foreach (OrtEpDevice device in OrtEnv.Instance().GetEpDevices())
        {
            if (string.Equals(device.EpName, providerName, StringComparison.OrdinalIgnoreCase) &&
                device.HardwareDevice.Type == type)
            {
                (uint, uint) id = (device.HardwareDevice.VendorId, device.HardwareDevice.DeviceId);
                if (!ids.Contains(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    private static void RegisterFromCatalog(string requestedName, string providerName, WINML_EXECUTION_TARGET_KIND kind)
    {
        Console.WriteLine($"  Preparing EP '{requestedName}' for {DeviceOptions.KindName(kind)} execution...");

        int ready = 0;
        int ensured = 0;
        int registered = 0;
        int notPresent = 0;
        int ensureFailed = 0;
        // FindAllProviders returns null rather than an empty array when no
        // provider is installed.
        ExecutionProvider[] providers = ExecutionProviderCatalog.GetDefault().FindAllProviders() ?? Array.Empty<ExecutionProvider>();
        foreach (ExecutionProvider provider in providers)
        {
            if (!string.Equals(provider.Name, providerName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(provider.Name, requestedName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Certification is not a selection gate for a provider the app
            // registers explicitly. Report it so the choice is informed.
            string certification = provider.Certification == ExecutionProviderCertification.Certified
                ? "certified"
                : "uncertified (app opt-in)";
            Console.Write($"  EP {provider.Name}: {certification}, ");

            switch (provider.ReadyState)
            {
                case ExecutionProviderReadyState.NotPresent:
                    ++notPresent;
                    Console.WriteLine("not installed -- skipped.");
                    continue;
                case ExecutionProviderReadyState.Ready:
                    ++ready;
                    Console.Write("ready");
                    break;
                case ExecutionProviderReadyState.NotReady:
                    ExecutionProviderReadyResult result = provider.EnsureReadyAsync().GetAwaiter().GetResult();
                    if (result.Status != ExecutionProviderReadyResultState.Success)
                    {
                        ++ensureFailed;
                        Console.WriteLine($"EnsureReady failed (0x{result.ExtendedError.HResult:X8}) -- skipped.");
                        continue;
                    }

                    ++ensured;
                    Console.Write("ensured");
                    break;
                default:
                    Console.WriteLine("unknown ready state -- skipped.");
                    continue;
            }

            // Registration fails harmlessly when the provider is already registered.
            try
            {
                OrtEnv.Instance().RegisterExecutionProviderLibrary(provider.Name, provider.LibraryPath);
                ++registered;
            }
            catch (OnnxRuntimeException)
            {
            }

            Console.WriteLine(", registered.");
        }

        Console.WriteLine(
            $"  EP summary: registered={registered}, ready={ready}, ensured={ensured}, notPresent={notPresent}, ensureFailed={ensureFailed}");
        if (ready == 0 && ensured == 0)
        {
            // Same HRESULT as the C++ helper: HRESULT_FROM_WIN32(ERROR_NOT_FOUND).
            throw new COMException($"Requested provider '{requestedName}' is not installed and ready.", unchecked((int)0x80070490));
        }
    }

    // Prints each provider device ONNX Runtime can see and returns whether the
    // named provider exposes a device of the required type.
    private static bool ReportDevices(string providerName, OrtHardwareDeviceType requiredType)
    {
        bool found = false;
        Console.WriteLine("  EP devices visible to the Runtime (provider -> hardware class):");
        foreach (OrtEpDevice device in OrtEnv.Instance().GetEpDevices())
        {
            OrtHardwareDeviceType type = device.HardwareDevice.Type;
            found |= type == requiredType && string.Equals(device.EpName, providerName, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"    {device.EpName} -> {type}");
        }

        return found;
    }

    private static OrtHardwareDeviceType HardwareType(WINML_EXECUTION_TARGET_KIND kind)
    {
        return kind switch
        {
            WINML_EXECUTION_TARGET_KIND.WINML_EXECUTION_TARGET_KIND_GPU => OrtHardwareDeviceType.GPU,
            WINML_EXECUTION_TARGET_KIND.WINML_EXECUTION_TARGET_KIND_NPU => OrtHardwareDeviceType.NPU,
            _ => OrtHardwareDeviceType.CPU,
        };
    }
}

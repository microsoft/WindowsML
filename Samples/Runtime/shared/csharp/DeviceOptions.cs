// Copyright (C) Microsoft Corporation. All rights reserved.
//
// The --device, --ep, --performance, --efficiency, and --verbose options that
// the C# samples share with the C++ samples.

using Microsoft.Windows.AI.MachineLearning.Runtime;

namespace WinMLSamples;

internal enum ExecutionPolicy
{
    Default,
    PreferPerformance,
    PreferEfficiency,
}

internal sealed class DeviceOptions
{
    public const string Usage = "[--device cpu|gpu|npu] [--ep <name>] [--performance|--efficiency] [--verbose]";

    public WINML_EXECUTION_TARGET_KIND Kind { get; private set; } = WINML_EXECUTION_TARGET_KIND.WINML_EXECUTION_TARGET_KIND_CPU;

    // An installed catalog provider, or a short alias such as openvino, to pin.
    public string? Ep { get; private set; }

    // Steers adapter selection when several adapters of the requested class exist.
    public ExecutionPolicy Policy { get; private set; }

    // Raises ONNX Runtime logging to show node placement and prints stage
    // execution diagnostics.
    public bool Verbose { get; private set; }

    // Consumes args[index] (and its value) when it is a device option.
    public bool TryParse(string[] args, ref int index)
    {
        string name = args[index];
        switch (name.ToLowerInvariant())
        {
            case "--device":
                Kind = ParseKind(Value(args, ref index));
                return true;
            case "--ep":
                Ep = Value(args, ref index);
                return true;
            case "--performance":
                Policy = ExecutionPolicy.PreferPerformance;
                return true;
            case "--efficiency":
                Policy = ExecutionPolicy.PreferEfficiency;
                return true;
            case "--verbose":
                Verbose = true;
                return true;
            default:
                return false;
        }
    }

    public static string KindName(WINML_EXECUTION_TARGET_KIND kind)
    {
        return kind switch
        {
            WINML_EXECUTION_TARGET_KIND.WINML_EXECUTION_TARGET_KIND_CPU => "CPU",
            WINML_EXECUTION_TARGET_KIND.WINML_EXECUTION_TARGET_KIND_GPU => "GPU",
            WINML_EXECUTION_TARGET_KIND.WINML_EXECUTION_TARGET_KIND_NPU => "NPU",
            _ => kind.ToString(),
        };
    }

    public static string Value(string[] args, ref int index)
    {
        string name = args[index];
        return index + 1 < args.Length ? args[++index] : throw new ArgumentException($"{name} requires a value.");
    }

    private static WINML_EXECUTION_TARGET_KIND ParseKind(string device)
    {
        return device.ToLowerInvariant() switch
        {
            "cpu" => WINML_EXECUTION_TARGET_KIND.WINML_EXECUTION_TARGET_KIND_CPU,
            "gpu" => WINML_EXECUTION_TARGET_KIND.WINML_EXECUTION_TARGET_KIND_GPU,
            "npu" => WINML_EXECUTION_TARGET_KIND.WINML_EXECUTION_TARGET_KIND_NPU,
            _ => throw new ArgumentException("--device must be cpu, gpu, or npu."),
        };
    }
}

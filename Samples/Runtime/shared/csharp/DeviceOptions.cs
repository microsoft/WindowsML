// Copyright (C) Microsoft Corporation. All rights reserved.
//
// The --device, --ep, --performance, --efficiency, and --verbose options that
// the C# samples share with the C++ samples.

using WinMLSamples.Runtime;

namespace WinMLSamples;

internal sealed class DeviceOptions
{
    public const string Usage = "[--device cpu|gpu|npu] [--ep <name>] [--performance|--efficiency] [--verbose]";

    public ExecutionTargetKind Kind { get; private set; } = ExecutionTargetKind.Cpu;

    // An installed catalog provider, or a short alias such as openvino, to pin.
    public string? Ep { get; private set; }

    // Steers adapter selection when several adapters of the requested class exist.
    public ExecutionTargetPreference Preference { get; private set; }

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
                Preference = ExecutionTargetPreference.Performance;
                return true;
            case "--efficiency":
                Preference = ExecutionTargetPreference.Efficiency;
                return true;
            case "--verbose":
                Verbose = true;
                return true;
            default:
                return false;
        }
    }

    public static string KindName(ExecutionTargetKind kind)
    {
        return kind switch
        {
            ExecutionTargetKind.Cpu => "CPU",
            ExecutionTargetKind.Gpu => "GPU",
            ExecutionTargetKind.Npu => "NPU",
            _ => kind.ToString(),
        };
    }

    public static string Value(string[] args, ref int index)
    {
        string name = args[index];
        return index + 1 < args.Length ? args[++index] : throw new ArgumentException($"{name} requires a value.");
    }

    private static ExecutionTargetKind ParseKind(string device)
    {
        return device.ToLowerInvariant() switch
        {
            "cpu" => ExecutionTargetKind.Cpu,
            "gpu" => ExecutionTargetKind.Gpu,
            "npu" => ExecutionTargetKind.Npu,
            _ => throw new ArgumentException("--device must be cpu, gpu, or npu."),
        };
    }
}

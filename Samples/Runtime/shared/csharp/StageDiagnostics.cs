// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Stage execution diagnostics shared by the C# samples. With --verbose, a
// sample enables diagnostics on a stage before Build and prints afterward
// where the stage's work runs: how much of it runs on the target's device,
// split into work shares by device and execution technology. For an ONNX
// Runtime stage it also prints the requested and selected provider.

using Microsoft.Windows.AI.MachineLearning.Runtime;
using Windows.Win32.Foundation;
using Win32 = Windows.Win32.PInvoke;

namespace WinMLSamples;

internal static unsafe class StageDiagnostics
{
    // Call before IWinMLPipelineBuilder::Build. Capturing diagnostics adds
    // work to Build, so it is off by default.
    public static void Enable(IWinMLStage* stage, ComObjects objects)
    {
        IWinMLStageExecutionDiagnostics* diagnostics = objects.QueryInterface<IWinMLStageExecutionDiagnostics>(stage);
        diagnostics->SetExecutionDiagnosticsEnabled(true);
    }

    // Call after Build on a stage passed to Enable.
    public static void Print(IWinMLStage* stage, ComObjects objects)
    {
        IWinMLStageExecutionDiagnostics* diagnostics = objects.QueryInterface<IWinMLStageExecutionDiagnostics>(stage);
        WINML_STAGE_PLACEMENT_COVERAGE coverage = diagnostics->GetPlacementCoverage();
        WINML_STAGE_WORK_UNIT unit = diagnostics->GetWorkUnit();
        Console.WriteLine($"  Placement coverage: {Trim(coverage, "WINML_STAGE_PLACEMENT_COVERAGE_")}");

        // The description is for logging only; do not parse it.
        diagnostics->GetPlacementDescription(out BOOL hasDescription, out PWSTR description);
        if (hasDescription)
        {
            try
            {
                Console.WriteLine($"  Placement: {description}");
            }
            finally
            {
                Win32.CoTaskMemFree(description.Value);
            }
        }

        uint shareCount = diagnostics->GetWorkShareCount();
        for (uint i = 0; i < shareCount; ++i)
        {
            IWinMLStageWorkShare* share = objects.Add(diagnostics->GetWorkShare(i));
            Console.WriteLine(
                $"    {share->GetUnitCount(),6} {Trim(unit, "WINML_STAGE_WORK_UNIT_")} on {DeviceOptions.KindName(share->GetDeviceKind())} " +
                $"through {Trim(share->GetImplementation(), "WINML_STAGE_WORK_IMPLEMENTATION_")}");
        }

        // ONNX Runtime stages also report the provider that served them.
        IWinMLOrtStageDiagnostics* ort = objects.TryQueryInterface<IWinMLOrtStageDiagnostics>(stage);
        if (ort != null)
        {
            PCWSTR requested;
            PCWSTR selected;
            if (ort->GetRequestedProviderName(&requested).Succeeded && ort->GetSelectedProviderName(&selected).Succeeded)
            {
                Console.WriteLine($"  Requested provider: {Name(requested)}, selected provider: {Name(selected)}");
            }
        }
    }

    private static string Name(PCWSTR name)
    {
        return name.Value == null ? "(runtime choice)" : name.ToString();
    }

    private static string Trim<T>(T value, string prefix)
        where T : struct, Enum
    {
        string text = value.ToString();
        return text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;
    }
}

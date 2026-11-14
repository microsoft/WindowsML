// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Stage execution diagnostics shared by the C# samples. With --verbose, a
// sample enables diagnostics on a stage before Build and prints afterward
// where the stage's work runs: how much of it runs on the target's device,
// split into work shares by device and execution technology. For an ONNX
// Runtime stage it also prints the requested and selected provider.

using WinMLSamples.Runtime;

namespace WinMLSamples;

internal static class StageDiagnostics
{
    // Call before PipelineBuilder.Build. Capturing diagnostics adds work to
    // Build, so it is off by default.
    public static void Enable(Stage stage)
    {
        using StageExecutionDiagnostics diagnostics = stage.As<StageExecutionDiagnostics>();
        diagnostics.ExecutionDiagnosticsEnabled = true;
    }

    // Call after Build on a stage passed to Enable.
    public static void Print(Stage stage)
    {
        using StageExecutionDiagnostics diagnostics = stage.As<StageExecutionDiagnostics>();
        Console.WriteLine($"  Placement coverage: {Name(diagnostics.PlacementCoverage)}");

        // The description is for logging only; do not parse it.
        diagnostics.GetPlacementDescription(out bool hasDescription, out string? description);
        if (hasDescription)
        {
            Console.WriteLine($"  Placement: {description}");
        }

        StageWorkUnit unit = diagnostics.WorkUnit;
        uint shareCount = diagnostics.WorkShareCount;
        for (uint i = 0; i < shareCount; ++i)
        {
            using StageWorkShare share = diagnostics.GetWorkShare(i);
            Console.WriteLine(
                $"    {share.UnitCount,6} {Name(unit)} on {DeviceOptions.KindName(share.DeviceKind)} " +
                $"through {Name(share.Implementation)}");
        }

        // ONNX Runtime stages also report the provider that served them. Other
        // stages answer the query but report no provider.
        if (stage.TryAs(out OrtStageDiagnostics? ort))
        {
            using (ort)
            {
                try
                {
                    Console.WriteLine(
                        $"  Requested provider: {ort.GetRequestedProviderName() ?? "(runtime choice)"}, " +
                        $"selected provider: {ort.GetSelectedProviderName() ?? "(runtime choice)"}");
                }
                // The provider names are informational, so a failed query skips the line.
                catch (Exception)
                {
                }
            }
        }
    }

    // Short C API constant names, such as GRAPH_NODE for WINML_STAGE_WORK_UNIT_GRAPH_NODE.
    private static string Name(StagePlacementCoverage coverage)
    {
        return coverage switch
        {
            StagePlacementCoverage.Full => "FULL",
            StagePlacementCoverage.Partial => "PARTIAL",
            StagePlacementCoverage.None => "NONE",
            _ => "UNKNOWN",
        };
    }

    private static string Name(StageWorkUnit unit)
    {
        return unit switch
        {
            StageWorkUnit.GraphNode => "GRAPH_NODE",
            StageWorkUnit.CompiledSubgraph => "COMPILED_SUBGRAPH",
            StageWorkUnit.ModelLayer => "MODEL_LAYER",
            StageWorkUnit.Stage => "STAGE",
            _ => "UNKNOWN",
        };
    }

    private static string Name(StageWorkImplementation implementation)
    {
        return implementation switch
        {
            StageWorkImplementation.Cpu => "CPU",
            StageWorkImplementation.D3D12Driver => "D3D12_DRIVER",
            StageWorkImplementation.D3D12Shader => "D3D12_SHADER",
            StageWorkImplementation.DirectML => "DIRECTML",
            StageWorkImplementation.WebGpu => "WEBGPU",
            StageWorkImplementation.VendorLibrary => "VENDOR_LIBRARY",
            _ => "UNKNOWN",
        };
    }
}

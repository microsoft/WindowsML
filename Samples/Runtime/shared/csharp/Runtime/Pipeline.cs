// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. Pipelines, stages, and the stage
// interfaces the samples query for: schema, sequence state, and execution
// diagnostics.

using Microsoft.Windows.AI.MachineLearning.Runtime;
using Windows.Win32.Foundation;
using Win32 = Windows.Win32.PInvoke;

namespace WinMLSamples.Runtime;

public sealed unsafe class PipelineBuilder : WinMLObject, IWinMLInterface<PipelineBuilder>
{
    internal PipelineBuilder(IWinMLPipelineBuilder* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLPipelineBuilder.IID_Guid;

    static PipelineBuilder IWinMLInterface<PipelineBuilder>.CreateFromOwnedPointer(nint pointer) => new((IWinMLPipelineBuilder*)pointer);

    private IWinMLPipelineBuilder* Self => (IWinMLPipelineBuilder*)ThisPtr;

    // Adds a stage that runs model on target. A null target lets the Runtime choose.
    public Stage AddModelStage(Model model, ExecutionTarget? target, string? debugName = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        IWinMLStage* stage = null;
        fixed (char* name = debugName)
        {
            Check(Self->AddModelStage(model.Raw, target is null ? null : target.Raw, new PCWSTR(name), &stage));
        }

        GC.KeepAlive(model);
        GC.KeepAlive(target);
        return new Stage(Required(stage));
    }

    // Validates every stage against its target and creates the pipeline.
    public Pipeline Build()
    {
        IWinMLPipeline* pipeline = null;
        Check(Self->Build(&pipeline));
        return new Pipeline(Required(pipeline));
    }
}

public sealed unsafe class Pipeline : WinMLObject, IWinMLInterface<Pipeline>
{
    internal Pipeline(IWinMLPipeline* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLPipeline.IID_Guid;

    static Pipeline IWinMLInterface<Pipeline>.CreateFromOwnedPointer(nint pointer) => new((IWinMLPipeline*)pointer);

    public void Run()
    {
        Check(((IWinMLPipeline*)ThisPtr)->Run());
    }

    // Discards state the stages carry between runs, such as a KV cache.
    public void ResetExecutionState()
    {
        Check(((IWinMLPipeline*)ThisPtr)->ResetExecutionState());
    }
}

public sealed unsafe class Stage : WinMLObject, IWinMLInterface<Stage>
{
    internal Stage(IWinMLStage* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLStage.IID_Guid;

    static Stage IWinMLInterface<Stage>.CreateFromOwnedPointer(nint pointer) => new((IWinMLStage*)pointer);

    private IWinMLStage* Self => (IWinMLStage*)ThisPtr;

    // The Runtime publishes only outputs requested before Build.
    public void RequestOutput(uint index)
    {
        Check(Self->RequestOutput(index));
    }

    // Binds by position: input ordinal, not an ONNX tensor name.
    public void BindInput(uint index, Tensor tensor)
    {
        ArgumentNullException.ThrowIfNull(tensor);
        Check(Self->BindInput(index, tensor.Raw));
        GC.KeepAlive(tensor);
    }

    public Tensor GetOutput(uint index)
    {
        IWinMLTensor* tensor = null;
        Check(Self->GetOutput(index, &tensor));
        return new Tensor(Required(tensor));
    }

    // The target the stage resolved to at Build.
    public ExecutionTarget GetExecutionTarget()
    {
        IWinMLExecutionTarget* target = null;
        Check(Self->GetExecutionTarget(&target));
        return new ExecutionTarget(Required(target));
    }
}

// The built stage's input and output tensor descriptions.
public sealed unsafe class StageSchema : WinMLObject, IWinMLInterface<StageSchema>
{
    private StageSchema(IWinMLStageSchema* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLStageSchema.IID_Guid;

    static StageSchema IWinMLInterface<StageSchema>.CreateFromOwnedPointer(nint pointer) => new((IWinMLStageSchema*)pointer);

    public TensorDescription GetInputTensorDescription(uint index)
    {
        WINML_TENSOR_DESC desc;
        Check(((IWinMLStageSchema*)ThisPtr)->GetInputTensorDesc(index, &desc));
        TensorDescription description = Tensor.ToDescription(desc);
        GC.KeepAlive(this);
        return description;
    }

    public TensorDescription GetOutputTensorDescription(uint index)
    {
        WINML_TENSOR_DESC desc;
        Check(((IWinMLStageSchema*)ThisPtr)->GetOutputTensorDesc(index, &desc));
        TensorDescription description = Tensor.ToDescription(desc);
        GC.KeepAlive(this);
        return description;
    }
}

// Sequence state of a built stateful stage, such as a decoder.
public sealed unsafe class StatefulStage : WinMLObject, IWinMLInterface<StatefulStage>
{
    private StatefulStage(IWinMLStatefulStage* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLStatefulStage.IID_Guid;

    static StatefulStage IWinMLInterface<StatefulStage>.CreateFromOwnedPointer(nint pointer) => new((IWinMLStatefulStage*)pointer);

    // The effective sequence capacity in tokens, or zero when unbounded.
    public ulong SequenceCapacity
    {
        get
        {
            ulong capacity;
            Check(((IWinMLStatefulStage*)ThisPtr)->GetSequenceCapacity(&capacity));
            return capacity;
        }
    }

    // The number of positions the stage holds.
    public ulong SequencePosition
    {
        get
        {
            ulong position;
            Check(((IWinMLStatefulStage*)ThisPtr)->GetSequencePosition(&position));
            return position;
        }
    }
}

// Stage state declarations, set before Build.
public sealed unsafe class StatefulStageOptions : WinMLObject, IWinMLInterface<StatefulStageOptions>
{
    private StatefulStageOptions(IWinMLStatefulStageOptions* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLStatefulStageOptions.IID_Guid;

    static StatefulStageOptions IWinMLInterface<StatefulStageOptions>.CreateFromOwnedPointer(nint pointer) => new((IWinMLStatefulStageOptions*)pointer);

    private IWinMLStatefulStageOptions* Self => (IWinMLStatefulStageOptions*)ThisPtr;

    public ulong SequenceCapacityHint
    {
        get
        {
            ulong capacity;
            Check(Self->GetSequenceCapacityHint(&capacity));
            return capacity;
        }

        set => Check(Self->SetSequenceCapacityHint(value));
    }

    // Declares that the Runtime carries output outputIndex into input
    // inputIndex between runs, as for a KV cache.
    public void AddStateTensorPair(uint inputIndex, uint outputIndex)
    {
        Check(Self->AddStateTensorPair(inputIndex, outputIndex));
    }
}

// Where a stage's work runs. Enable it before Build and read it afterward.
public sealed unsafe class StageExecutionDiagnostics : WinMLObject, IWinMLInterface<StageExecutionDiagnostics>
{
    private StageExecutionDiagnostics(IWinMLStageExecutionDiagnostics* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLStageExecutionDiagnostics.IID_Guid;

    static StageExecutionDiagnostics IWinMLInterface<StageExecutionDiagnostics>.CreateFromOwnedPointer(nint pointer) => new((IWinMLStageExecutionDiagnostics*)pointer);

    private IWinMLStageExecutionDiagnostics* Self => (IWinMLStageExecutionDiagnostics*)ThisPtr;

    // Capturing diagnostics adds work to Build, so it is off by default.
    public bool ExecutionDiagnosticsEnabled
    {
        get
        {
            BOOL enabled;
            Check(Self->GetExecutionDiagnosticsEnabled(&enabled));
            return enabled;
        }

        set => Check(Self->SetExecutionDiagnosticsEnabled(value));
    }

    public StagePlacementCoverage PlacementCoverage
    {
        get
        {
            WINML_STAGE_PLACEMENT_COVERAGE coverage;
            Check(Self->GetPlacementCoverage(&coverage));
            return (StagePlacementCoverage)coverage;
        }
    }

    public StageWorkUnit WorkUnit
    {
        get
        {
            WINML_STAGE_WORK_UNIT unit;
            Check(Self->GetWorkUnit(&unit));
            return (StageWorkUnit)unit;
        }
    }

    public uint WorkShareCount
    {
        get
        {
            uint count;
            Check(Self->GetWorkShareCount(&count));
            return count;
        }
    }

    // A description for logging only; do not parse it.
    public void GetPlacementDescription(out bool hasDescription, out string? description)
    {
        BOOL has;
        PWSTR text;
        Check(Self->GetPlacementDescription(&has, &text));
        try
        {
            hasDescription = has;
            description = text.Value == null ? null : new string(text.Value);
        }
        finally
        {
            Win32.CoTaskMemFree(text.Value);
        }
    }

    public StageWorkShare GetWorkShare(uint index)
    {
        IWinMLStageWorkShare* share = null;
        Check(Self->GetWorkShare(index, &share));
        return new StageWorkShare(Required(share));
    }
}

// One share of a stage's work: units on one device through one technology.
public sealed unsafe class StageWorkShare : WinMLObject, IWinMLInterface<StageWorkShare>
{
    internal StageWorkShare(IWinMLStageWorkShare* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLStageWorkShare.IID_Guid;

    static StageWorkShare IWinMLInterface<StageWorkShare>.CreateFromOwnedPointer(nint pointer) => new((IWinMLStageWorkShare*)pointer);

    private IWinMLStageWorkShare* Self => (IWinMLStageWorkShare*)ThisPtr;

    public ExecutionTargetKind DeviceKind
    {
        get
        {
            WINML_EXECUTION_TARGET_KIND kind;
            Check(Self->GetDeviceKind(&kind));
            return (ExecutionTargetKind)kind;
        }
    }

    public StageWorkImplementation Implementation
    {
        get
        {
            WINML_STAGE_WORK_IMPLEMENTATION implementation;
            Check(Self->GetImplementation(&implementation));
            return (StageWorkImplementation)implementation;
        }
    }

    public uint UnitCount
    {
        get
        {
            uint count;
            Check(Self->GetUnitCount(&count));
            return count;
        }
    }
}

// The ONNX Runtime provider that a stage requested and the one that served it.
public sealed unsafe class OrtStageDiagnostics : WinMLObject, IWinMLInterface<OrtStageDiagnostics>
{
    private OrtStageDiagnostics(IWinMLOrtStageDiagnostics* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLOrtStageDiagnostics.IID_Guid;

    static OrtStageDiagnostics IWinMLInterface<OrtStageDiagnostics>.CreateFromOwnedPointer(nint pointer) => new((IWinMLOrtStageDiagnostics*)pointer);

    // Null when the Runtime chose.
    public string? GetRequestedProviderName()
    {
        PCWSTR name;
        Check(((IWinMLOrtStageDiagnostics*)ThisPtr)->GetRequestedProviderName(&name));
        string? result = name.Value == null ? null : name.ToString();
        GC.KeepAlive(this);
        return result;
    }

    public string? GetSelectedProviderName()
    {
        PCWSTR name;
        Check(((IWinMLOrtStageDiagnostics*)ThisPtr)->GetSelectedProviderName(&name));
        string? result = name.Value == null ? null : name.ToString();
        GC.KeepAlive(this);
        return result;
    }
}

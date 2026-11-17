// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. The Runtime root object, execution
// targets, and models. See ..\README.md.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Native = Microsoft.Windows.AI.MachineLearning.Runtime.PInvoke;

namespace WinMLSamples.Runtime;

// The Runtime: the factory for models, targets, and pipeline builders.
public sealed unsafe class WinMLRuntime : WinMLObject, IWinMLInterface<WinMLRuntime>
{
    private WinMLRuntime(IWinMLRuntime* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLRuntime.IID_Guid;

    private IWinMLRuntime* Self => (IWinMLRuntime*)ThisPtr;

    public static WinMLRuntime Create()
    {
        IWinMLRuntime* runtime = null;
        Guid iid = InterfaceId;
        WinMLException.ThrowIfFailed(Native.WinMLCreateRuntime(&iid, (void**)&runtime));
        return new WinMLRuntime(Required(runtime));
    }

    static WinMLRuntime IWinMLInterface<WinMLRuntime>.CreateFromOwnedPointer(nint pointer) => new((IWinMLRuntime*)pointer);

    public ExecutionTarget CreateCpuExecutionTarget()
    {
        IWinMLExecutionTarget* target = null;
        Check(Self->CreateCpuExecutionTarget(&target));
        return new ExecutionTarget(Required(target));
    }

    // Creates a target of a device class. Throws WinMLException with
    // WinMLException.NotFound when the device has none.
    public ExecutionTarget CreateExecutionTarget(ExecutionTargetKind kind, ExecutionTargetPreference preference = ExecutionTargetPreference.Default)
    {
        IWinMLExecutionTarget* target = null;
        Check(Self->CreateExecutionTarget((WINML_EXECUTION_TARGET_KIND)kind, (WINML_EXECUTION_TARGET_PREFERENCE)preference, &target));
        return new ExecutionTarget(Required(target));
    }

    // Returns false when the device has no adapter of the class
    // (HRESULT_FROM_WIN32(ERROR_NOT_FOUND)); other failures throw.
    public bool TryCreateExecutionTarget(ExecutionTargetKind kind, ExecutionTargetPreference preference, [NotNullWhen(true)] out ExecutionTarget? target)
    {
        IWinMLExecutionTarget* created = null;
        HRESULT hr = Self->CreateExecutionTarget((WINML_EXECUTION_TARGET_KIND)kind, (WINML_EXECUTION_TARGET_PREFERENCE)preference, &created);
        if (hr.Value == WinMLException.NotFound)
        {
            GC.KeepAlive(this);
            Release(created);
            target = null;
            return false;
        }

        Check(hr);
        target = new ExecutionTarget(Required(created));
        return true;
    }

    // Creates a target on one DXCore adapter. The target keeps the adapter alive.
    public ExecutionTarget CreateExecutionTarget(DXCoreAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        IWinMLExecutionTarget* target = null;
        Check(Self->CreateExecutionTargetFromAdapter((IUnknown*)adapter.NativePointer, &target));
        GC.KeepAlive(adapter);
        ExecutionTarget result = new(Required(target));
        result.HoldReference(adapter);
        return result;
    }

    // Creates a target pinned to an ONNX Runtime execution provider that the
    // app registered. A hardware target selects the device; it can come from
    // CreateExecutionTarget(DXCoreAdapter).
    public ExecutionTarget CreateExecutionTarget(string executionProviderName, ExecutionTargetKind kind, ExecutionTarget? hardwareTarget = null)
    {
        using OrtCompatibility compatibility = As<OrtCompatibility>();
        return compatibility.CreateExecutionTarget(executionProviderName, kind, hardwareTarget);
    }

    // Loads a single-file model. The artifact selects the engine: ONNX
    // Runtime for .onnx and .ort, llama.cpp for .gguf.
    public Model LoadModelFromFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        IWinMLModel* model = null;
        fixed (char* pathPointer = path)
        {
            Check(Self->LoadModelFromFile(new PCWSTR(pathPointer), null, &model));
        }

        return new Model(Required(model));
    }

    // Loads a model from a folder, such as a checkpoint with config.json and
    // safetensors weights.
    public Model LoadModelFromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        IWinMLModel* model = null;
        fixed (char* pathPointer = path)
        {
            Check(Self->LoadModelFromPath(new PCWSTR(pathPointer), null, &model));
        }

        return new Model(Required(model));
    }

    public PipelineBuilder CreatePipelineBuilder()
    {
        IWinMLPipelineBuilder* builder = null;
        Check(Self->CreatePipelineBuilder(&builder));
        return new PipelineBuilder(Required(builder));
    }

    internal IWinMLRuntime* Raw => Self;
}

// The ONNX Runtime continuity interface of the Runtime.
public sealed unsafe class OrtCompatibility : WinMLObject, IWinMLInterface<OrtCompatibility>
{
    private OrtCompatibility(IWinMLOrtCompatibility* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLOrtCompatibility.IID_Guid;

    static OrtCompatibility IWinMLInterface<OrtCompatibility>.CreateFromOwnedPointer(nint pointer) => new((IWinMLOrtCompatibility*)pointer);

    // A provider-pinned target fails to initialize when the provider cannot
    // run on the requested device class. The target keeps hardwareTarget alive.
    public ExecutionTarget CreateExecutionTarget(string executionProviderName, ExecutionTargetKind kind, ExecutionTarget? hardwareTarget = null)
    {
        ArgumentNullException.ThrowIfNull(executionProviderName);
        IWinMLExecutionTarget* target = null;
        IWinMLExecutionTarget* hardware = hardwareTarget is null ? null : (IWinMLExecutionTarget*)hardwareTarget.NativePointer;
        fixed (char* name = executionProviderName)
        {
            Check(((IWinMLOrtCompatibility*)ThisPtr)->CreateExecutionTarget(new PCWSTR(name), (WINML_EXECUTION_TARGET_KIND)kind, hardware, &target));
        }

        GC.KeepAlive(hardwareTarget);
        ExecutionTarget result = new(Required(target));
        if (hardwareTarget is not null)
        {
            result.HoldReference(hardwareTarget);
        }

        return result;
    }
}

// The device that runs a stage, and the factory for tensors on that device.
public sealed unsafe partial class ExecutionTarget : WinMLObject, IWinMLInterface<ExecutionTarget>
{
    internal ExecutionTarget(IWinMLExecutionTarget* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLExecutionTarget.IID_Guid;

    static ExecutionTarget IWinMLInterface<ExecutionTarget>.CreateFromOwnedPointer(nint pointer) => new((IWinMLExecutionTarget*)pointer);

    public ExecutionTargetKind Kind
    {
        get
        {
            WINML_EXECUTION_TARGET_KIND kind;
            Check(((IWinMLExecutionTarget*)ThisPtr)->GetKind(&kind));
            return (ExecutionTargetKind)kind;
        }
    }

    internal IWinMLExecutionTarget* Raw => (IWinMLExecutionTarget*)ThisPtr;
}

// A loaded model. Query it for ModelSchema to read its declared inputs and outputs.
public sealed unsafe class Model : WinMLObject, IWinMLInterface<Model>
{
    internal Model(IWinMLModel* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLModel.IID_Guid;

    static Model IWinMLInterface<Model>.CreateFromOwnedPointer(nint pointer) => new((IWinMLModel*)pointer);

    internal IWinMLModel* Raw => (IWinMLModel*)ThisPtr;
}

public sealed unsafe class ModelSchema : WinMLObject, IWinMLInterface<ModelSchema>
{
    private ModelSchema(IWinMLModelSchema* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLModelSchema.IID_Guid;

    static ModelSchema IWinMLInterface<ModelSchema>.CreateFromOwnedPointer(nint pointer) => new((IWinMLModelSchema*)pointer);

    private IWinMLModelSchema* Self => (IWinMLModelSchema*)ThisPtr;

    public uint InputCount
    {
        get
        {
            uint count;
            Check(Self->GetInputCount(&count));
            return count;
        }
    }

    public uint OutputCount
    {
        get
        {
            uint count;
            Check(Self->GetOutputCount(&count));
            return count;
        }
    }

    public TensorSchemaDescription GetInputTensorDescription(uint index)
    {
        WINML_TENSOR_SCHEMA_DESC desc;
        Check(Self->GetInputTensorDesc(index, &desc));
        return ToDescription(desc);
    }

    public TensorSchemaDescription GetOutputTensorDescription(uint index)
    {
        WINML_TENSOR_SCHEMA_DESC desc;
        Check(Self->GetOutputTensorDesc(index, &desc));
        return ToDescription(desc);
    }

    // The dimensions point into the schema's storage, so this object must
    // stay alive until they are copied.
    private TensorSchemaDescription ToDescription(in WINML_TENSOR_SCHEMA_DESC desc)
    {
        TensorSchemaDescription description = new((TensorDataType)desc.dataType, new ReadOnlySpan<ulong>(desc.dimensions, (int)desc.dimensionCount).ToArray());
        GC.KeepAlive(this);
        return description;
    }
}

// The explicit llama.cpp route: query the Runtime for it with TryAs. Present
// when the Windows ML package includes the llama.cpp engine.
public sealed unsafe class LlamaModelLoader : WinMLObject, IWinMLInterface<LlamaModelLoader>
{
    private LlamaModelLoader(IWinMLLlamaModelLoader* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLLlamaModelLoader.IID_Guid;

    static LlamaModelLoader IWinMLInterface<LlamaModelLoader>.CreateFromOwnedPointer(nint pointer) => new((IWinMLLlamaModelLoader*)pointer);

    public Model LoadModelFromFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        IWinMLModel* model = null;
        fixed (char* pathPointer = path)
        {
            Check(((IWinMLLlamaModelLoader*)ThisPtr)->LoadModelFromFile(new PCWSTR(pathPointer), null, &model));
        }

        return new Model(Required(model));
    }
}

// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. The text generation Task API: the Task
// root object, configurations, tasks, sessions, and pull streams. A session
// and the streams it returns are bound to the thread that created the
// session; TextGenerator owns one on a SessionThread for use from any thread.

using System.Runtime.InteropServices;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Microsoft.Windows.AI.MachineLearning.Runtime.Tasks;
using Windows.Win32.Foundation;
using Native = Microsoft.Windows.AI.MachineLearning.Runtime.PInvoke;

namespace WinMLSamples.Runtime.Tasks;

public enum TextGenerationFinishReason
{
    EosToken = 0,
    StopToken = 1,
    MaxTokens = 2,
    SequenceCapacity = 3,
    Canceled = 4,
    Error = 5,
}

public enum CompiledProgramStorage
{
    Programs = 0,
    ProgramsAndWeights = 1,
}

public enum TextGenerationSpeculativeMethod
{
    None = 0,
    Model = 1,
    DraftModel = 2,
    PromptLookup = 3,
}

public enum TextGenerationSessionState
{
    Empty = 0,
    Ready = 1,
    Active = 2,
    ResetRequired = 3,
    Closed = 4,
}

// Whether a configuration binds one pipeline or separate prefill and decode pipelines.
public enum TextGenerationConfigurationMode
{
    Unified = 0,
    PrefillDecode = 1,
}

// The pipeline a configuration setter binds: Unified, or Prefill or Decode in
// PrefillDecode mode.
public enum TextGenerationPipelinePhase
{
    Unified = 0,
    Prefill = 1,
    Decode = 2,
}

public enum TextGenerationOutputKind
{
    Logits = 0,
    SampledToken = 1,
}

public enum TextGenerationDecodeInputMode
{
    CallerBound = 0,
    PipelineFeedback = 1,
}

public enum TextGenerationExecutionProfile
{
    SinglePipeline = 0,
    PrefillDecodeCallerBound = 1,
    PrefillDecodePipelineFeedback = 2,
}

public enum TextGenerationPrefillProfile
{
    Default = 0,
    Compact = 1,
}

// Default applies a configured watermark; Disabled opts one request out.
public enum TextGenerationWatermarkMode
{
    Default = 0,
    Disabled = 1,
}

// Speculative decoding counts for one generation.
public readonly record struct TextGenerationSpeculativeStatistics(
    TextGenerationSpeculativeMethod Method,
    uint VerificationStepCount,
    uint DraftTokenCount,
    uint AcceptedDraftTokenCount);

// How SetModel prepares a model. Unset properties keep the Runtime defaults.
public sealed class TextGenerationModelOptions
{
    // A directory that keeps the programs sessions compile for the GPU.
    public string? CompiledProgramDirectory { get; init; }

    public CompiledProgramStorage? CompiledProgramStorage { get; init; }

    // A smaller model that shares the model's tokenizer, for speculative decoding.
    public Model? DraftModel { get; init; }

    public TextGenerationPrefillProfile? PrefillProfile { get; init; }
}

public sealed class TextGenerationSpeculativeOptions
{
    public TextGenerationSpeculativeMethod Method { get; init; }

    // Zero lets the Runtime choose.
    public uint DraftTokenCount { get; init; }
}

// Generation settings for CreateTextGenerationOptions. Unset properties keep
// the Runtime defaults.
public sealed class TextGenerationOptionValues
{
    public uint? MaxNewTokens { get; init; }

    public float? Temperature { get; init; }

    public uint? TopK { get; init; }

    public float? TopP { get; init; }

    public float? MinP { get; init; }

    public float? RepetitionPenalty { get; init; }

    public uint? RepetitionPenaltyWindow { get; init; }

    public float? PresencePenalty { get; init; }

    public float? FrequencyPenalty { get; init; }

    public ulong? Seed { get; init; }

    public TextGenerationWatermarkMode? WatermarkMode { get; init; }

    // Tokens that end generation, in addition to the task's end-of-sequence tokens.
    public IReadOnlyList<uint>? StopTokenIds { get; init; }
}

// What a text generation session supports, and the sequence it holds.
public readonly record struct TextGenerationCapabilities(
    bool SupportsContinuation,
    bool SupportsConstraints,
    bool SupportsSegmentedPrompts,
    bool SupportsEmbeddingSegments = false,
    bool SupportsConstrainedContinuation = false,
    bool SupportsConstrainedEmbeddingSegments = false,
    TextGenerationExecutionProfile ExecutionProfile = TextGenerationExecutionProfile.SinglePipeline,
    TextGenerationOutputKind OutputKind = TextGenerationOutputKind.Logits,
    TextGenerationSequenceState SequenceState = default);

// The Task API root object.
public sealed unsafe class WinMLTasks : WinMLObject, IWinMLInterface<WinMLTasks>
{
    private TextGenerationTaskFactory? _textGeneration;
    private ChatCompletionTaskFactory? _chatCompletion;
    private AutomaticSpeechRecognitionTaskFactory? _automaticSpeechRecognition;
    private DecisionTaskFactory? _decision;

    private WinMLTasks(IWinMLTasks* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTasks.IID_Guid;

    static WinMLTasks IWinMLInterface<WinMLTasks>.CreateFromOwnedPointer(nint pointer) => new((IWinMLTasks*)pointer);

    // The factory for text generation configurations, options, and tasks.
    public TextGenerationTaskFactory TextGeneration => GetCachedInterface(ref _textGeneration);

    // The factory for chat completion tasks, which compose a tokenizer's chat
    // template with a text generation session.
    public ChatCompletionTaskFactory ChatCompletion => GetCachedInterface(ref _chatCompletion);

    // The factory for speech recognition tasks. Throws InvalidCastException
    // with E_NOINTERFACE when the package has no speech recognition Task.
    public AutomaticSpeechRecognitionTaskFactory AutomaticSpeechRecognition => GetCachedInterface(ref _automaticSpeechRecognition);

    // The factory for typed decision tasks. Throws InvalidCastException with
    // E_NOINTERFACE when the package has no decision Task.
    public DecisionTaskFactory Decision => GetCachedInterface(ref _decision);

    public static WinMLTasks Create(WinMLRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        IWinMLTasks* tasks = null;
        Guid iid = InterfaceId;
        WinMLException.ThrowIfFailed(Native.WinMLCreateTasks(runtime.Raw, &iid, (void**)&tasks));
        GC.KeepAlive(runtime);
        return new WinMLTasks(Required(tasks));
    }

    public CancellationSource CreateCancellationSource()
    {
        IWinMLCancellationSource* cancellation = null;
        Check(((IWinMLTasks*)ThisPtr)->CreateCancellationSource(&cancellation));
        return new CancellationSource(Required(cancellation));
    }

    // The Runtime the Task API was created with.
    public WinMLRuntime GetRuntime()
    {
        IWinMLRuntime* runtime = null;
        Check(((IWinMLTasks*)ThisPtr)->GetRuntime(&runtime));
        return new WinMLRuntime(Required(runtime));
    }

    private protected override void OnDisposing()
    {
        DisposeCached(_textGeneration);
        DisposeCached(_chatCompletion);
        DisposeCached(_automaticSpeechRecognition);
        DisposeCached(_decision);
    }
}

// Cancels generation from any thread.
public sealed unsafe class CancellationSource : WinMLObject, IWinMLInterface<CancellationSource>
{
    internal CancellationSource(IWinMLCancellationSource* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLCancellationSource.IID_Guid;

    static CancellationSource IWinMLInterface<CancellationSource>.CreateFromOwnedPointer(nint pointer) => new((IWinMLCancellationSource*)pointer);

    internal IWinMLCancellationSource* Raw => (IWinMLCancellationSource*)ThisPtr;

    public void Cancel()
    {
        Check(Raw->Cancel());
    }

    public bool IsCanceled
    {
        get
        {
            BOOL canceled;
            Check(Raw->GetIsCanceled(&canceled));
            return canceled;
        }
    }
}

public sealed unsafe class TextGenerationTaskFactory : WinMLObject, IWinMLInterface<TextGenerationTaskFactory>
{
    private TextGenerationTaskFactory(IWinMLTextGenerationTaskFactory* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTextGenerationTaskFactory.IID_Guid;

    static TextGenerationTaskFactory IWinMLInterface<TextGenerationTaskFactory>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLTextGenerationTaskFactory*)pointer);

    private IWinMLTextGenerationTaskFactory* Self => (IWinMLTextGenerationTaskFactory*)ThisPtr;

    // A configuration describes one model; a session consumes it.
    public TextGenerationConfiguration CreateTextGenerationConfiguration()
    {
        IWinMLTextGenerationConfiguration* configuration = null;
        Check(Self->CreateTextGenerationConfiguration(&configuration));
        return new TextGenerationConfiguration(Required(configuration));
    }

    // Options for greedy generation of at most maxNewTokens tokens.
    public TextGenerationOptions CreateTextGenerationOptions(uint maxNewTokens)
    {
        WINML_TEXT_GENERATION_OPTIONS values = new()
        {
            presentFields = WINML_TEXT_GENERATION_OPTION_FIELDS.WINML_TEXT_GENERATION_OPTION_FIELD_MAX_NEW_TOKENS,
            maxNewTokens = maxNewTokens,
        };
        IWinMLTextGenerationOptions* options = null;
        Check(Self->CreateTextGenerationOptions(&values, 0, null, &options));
        return new TextGenerationOptions(Required(options));
    }

    // Options with the given settings and stop tokens.
    public TextGenerationOptions CreateTextGenerationOptions(TextGenerationOptionValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        WINML_TEXT_GENERATION_OPTIONS native = default;
        if (values.MaxNewTokens is uint maxNewTokens)
        {
            native.presentFields |= WINML_TEXT_GENERATION_OPTION_FIELDS.WINML_TEXT_GENERATION_OPTION_FIELD_MAX_NEW_TOKENS;
            native.maxNewTokens = maxNewTokens;
        }

        if (values.Temperature is float temperature)
        {
            native.presentFields |= WINML_TEXT_GENERATION_OPTION_FIELDS.WINML_TEXT_GENERATION_OPTION_FIELD_TEMPERATURE;
            native.temperature = temperature;
        }

        if (values.TopK is uint topK)
        {
            native.presentFields |= WINML_TEXT_GENERATION_OPTION_FIELDS.WINML_TEXT_GENERATION_OPTION_FIELD_TOP_K;
            native.topK = topK;
        }

        if (values.TopP is float topP)
        {
            native.presentFields |= WINML_TEXT_GENERATION_OPTION_FIELDS.WINML_TEXT_GENERATION_OPTION_FIELD_TOP_P;
            native.topP = topP;
        }

        if (values.MinP is float minP)
        {
            native.presentFields |= WINML_TEXT_GENERATION_OPTION_FIELDS.WINML_TEXT_GENERATION_OPTION_FIELD_MIN_P;
            native.minP = minP;
        }

        if (values.RepetitionPenalty is float repetitionPenalty)
        {
            native.presentFields |= WINML_TEXT_GENERATION_OPTION_FIELDS.WINML_TEXT_GENERATION_OPTION_FIELD_REPETITION_PENALTY;
            native.repetitionPenalty = repetitionPenalty;
        }

        if (values.RepetitionPenaltyWindow is uint repetitionPenaltyWindow)
        {
            native.presentFields |= WINML_TEXT_GENERATION_OPTION_FIELDS.WINML_TEXT_GENERATION_OPTION_FIELD_REPETITION_PENALTY_WINDOW;
            native.repetitionPenaltyWindow = repetitionPenaltyWindow;
        }

        if (values.PresencePenalty is float presencePenalty)
        {
            native.presentFields |= WINML_TEXT_GENERATION_OPTION_FIELDS.WINML_TEXT_GENERATION_OPTION_FIELD_PRESENCE_PENALTY;
            native.presencePenalty = presencePenalty;
        }

        if (values.FrequencyPenalty is float frequencyPenalty)
        {
            native.presentFields |= WINML_TEXT_GENERATION_OPTION_FIELDS.WINML_TEXT_GENERATION_OPTION_FIELD_FREQUENCY_PENALTY;
            native.frequencyPenalty = frequencyPenalty;
        }

        if (values.Seed is ulong seed)
        {
            native.presentFields |= WINML_TEXT_GENERATION_OPTION_FIELDS.WINML_TEXT_GENERATION_OPTION_FIELD_SEED;
            native.seed = seed;
        }

        if (values.WatermarkMode is TextGenerationWatermarkMode watermarkMode)
        {
            native.presentFields |= WINML_TEXT_GENERATION_OPTION_FIELDS.WINML_TEXT_GENERATION_OPTION_FIELD_WATERMARK_MODE;
            native.watermarkMode = (WINML_TEXT_GENERATION_WATERMARK_MODE)watermarkMode;
        }

        uint[] stopTokenIds = values.StopTokenIds?.ToArray() ?? [];
        IWinMLTextGenerationOptions* options = null;
        fixed (uint* stopTokens = stopTokenIds)
        {
            Check(Self->CreateTextGenerationOptions(&native, (uint)stopTokenIds.Length, stopTokenIds.Length == 0 ? null : stopTokens, &options));
        }

        return new TextGenerationOptions(Required(options));
    }

    // A task that stops at the tokenizer's end-of-sequence tokens.
    public TextGenerationTask CreateTextGenerationTask(Tokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        IWinMLTextGenerationTask* task = null;
        Check(Self->CreateTextGenerationTask(
            tokenizer.Raw,
            WINML_TEXT_GENERATION_EOS_POLICY.WINML_TEXT_GENERATION_EOS_POLICY_TOKENIZER_DEFAULT,
            0,
            null,
            &task));
        GC.KeepAlive(tokenizer);
        return new TextGenerationTask(Required(task));
    }

    // A task that stops at eosTokenIds instead of the tokenizer's
    // end-of-sequence tokens. An empty list stops only at stop tokens, the
    // token limit, or the sequence capacity.
    public TextGenerationTask CreateTextGenerationTask(Tokenizer tokenizer, ReadOnlySpan<uint> eosTokenIds)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        IWinMLTextGenerationTask* task = null;
        fixed (uint* eosTokens = eosTokenIds)
        {
            Check(Self->CreateTextGenerationTask(
                tokenizer.Raw,
                WINML_TEXT_GENERATION_EOS_POLICY.WINML_TEXT_GENERATION_EOS_POLICY_EXPLICIT,
                (uint)eosTokenIds.Length,
                eosTokenIds.IsEmpty ? null : eosTokens,
                &task));
        }

        GC.KeepAlive(tokenizer);
        return new TextGenerationTask(Required(task));
    }
}

public sealed unsafe class TextGenerationOptions : WinMLObject, IWinMLInterface<TextGenerationOptions>
{
    internal TextGenerationOptions(IWinMLTextGenerationOptions* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTextGenerationOptions.IID_Guid;

    static TextGenerationOptions IWinMLInterface<TextGenerationOptions>.CreateFromOwnedPointer(nint pointer) => new((IWinMLTextGenerationOptions*)pointer);

    internal IWinMLTextGenerationOptions* Raw => (IWinMLTextGenerationOptions*)ThisPtr;

    // The resolved settings, every field set, including the stop tokens.
    public TextGenerationOptionValues GetValues()
    {
        WINML_TEXT_GENERATION_OPTIONS values;
        Check(Raw->GetValues(&values));
        return new TextGenerationOptionValues
        {
            MaxNewTokens = values.maxNewTokens,
            Temperature = values.temperature,
            TopK = values.topK,
            TopP = values.topP,
            MinP = values.minP,
            RepetitionPenalty = values.repetitionPenalty,
            RepetitionPenaltyWindow = values.repetitionPenaltyWindow,
            PresencePenalty = values.presencePenalty,
            FrequencyPenalty = values.frequencyPenalty,
            Seed = values.seed,
            WatermarkMode = (TextGenerationWatermarkMode)values.watermarkMode,
            StopTokenIds = GetStopTokenIds(),
        };
    }

    public uint[] GetStopTokenIds()
    {
        uint count = 0;
        uint* tokens = null;
        Check(Raw->GetStopTokenIds(&count, &tokens));
        return Tokenizer.TakeTokenIds(tokens, count);
    }
}

public sealed unsafe class TextGenerationConfiguration : WinMLObject, IWinMLInterface<TextGenerationConfiguration>
{
    internal TextGenerationConfiguration(IWinMLTextGenerationConfiguration* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTextGenerationConfiguration.IID_Guid;

    static TextGenerationConfiguration IWinMLInterface<TextGenerationConfiguration>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLTextGenerationConfiguration*)pointer);

    internal IWinMLTextGenerationConfiguration* Raw => (IWinMLTextGenerationConfiguration*)ThisPtr;

    // The largest sequence, in tokens, that a session holds.
    public ulong LogicalCapacity
    {
        get
        {
            ulong capacity;
            Check(Raw->GetLogicalCapacity(&capacity));
            return capacity;
        }

        set => Check(Raw->SetLogicalCapacity(value));
    }

    // The tokenizer the configuration uses, such as the one SetModel created.
    public Tokenizer GetTokenizer()
    {
        IWinMLTokenizer* tokenizer = null;
        Check(Raw->GetTokenizer(&tokenizer));
        return new Tokenizer(Required(tokenizer));
    }

    // The tokenizer that encodes prompts and decodes generated tokens; it must
    // be the task's tokenizer.
    public void SetTokenizer(Tokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        Check(Raw->SetTokenizer(tokenizer.Raw));
        GC.KeepAlive(tokenizer);
    }

    // Select the mode before binding pipelines and endpoints.
    public TextGenerationConfigurationMode Mode
    {
        get
        {
            WINML_TEXT_GENERATION_CONFIGURATION_MODE mode;
            Check(Raw->GetMode(&mode));
            return (TextGenerationConfigurationMode)mode;
        }

        set => Check(Raw->SetMode((WINML_TEXT_GENERATION_CONFIGURATION_MODE)value));
    }

    // How each decode step receives its token in PrefillDecode mode.
    public TextGenerationDecodeInputMode DecodeInputMode
    {
        get
        {
            WINML_TEXT_GENERATION_DECODE_INPUT_MODE mode;
            Check(Raw->GetDecodeInputMode(&mode));
            return (TextGenerationDecodeInputMode)mode;
        }

        set => Check(Raw->SetDecodeInputMode((WINML_TEXT_GENERATION_DECODE_INPUT_MODE)value));
    }

    public void SetPipeline(TextGenerationPipelinePhase phase, Pipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        Check(Raw->SetPipeline((WINML_TEXT_GENERATION_PIPELINE_PHASE)phase, pipeline.Raw));
        GC.KeepAlive(pipeline);
    }

    // The getters below throw InvalidOperationException (E_NOT_VALID_STATE)
    // when nothing is bound, as before SetModel's session composes its objects.
    public Pipeline GetPipeline(TextGenerationPipelinePhase phase)
    {
        IWinMLPipeline* pipeline = null;
        Check(Raw->GetPipeline((WINML_TEXT_GENERATION_PIPELINE_PHASE)phase, &pipeline));
        return new Pipeline(Required(pipeline));
    }

    // The stage input that takes token IDs.
    public void SetTokenInput(TextGenerationPipelinePhase phase, Stage stage, uint inputIndex)
    {
        ArgumentNullException.ThrowIfNull(stage);
        Check(Raw->SetTokenInput((WINML_TEXT_GENERATION_PIPELINE_PHASE)phase, stage.Raw, inputIndex));
        GC.KeepAlive(stage);
    }

    public Stage GetTokenInput(TextGenerationPipelinePhase phase, out uint inputIndex)
    {
        IWinMLStage* stage = null;
        uint index = 0;
        Check(Raw->GetTokenInput((WINML_TEXT_GENERATION_PIPELINE_PHASE)phase, &stage, &index));
        inputIndex = index;
        return new Stage(Required(stage));
    }

    // The stage output that produces logits or sampled tokens.
    public void SetOutput(TextGenerationPipelinePhase phase, Stage stage, uint outputIndex, TextGenerationOutputKind kind)
    {
        ArgumentNullException.ThrowIfNull(stage);
        Check(Raw->SetOutput((WINML_TEXT_GENERATION_PIPELINE_PHASE)phase, stage.Raw, outputIndex, (WINML_TEXT_GENERATION_OUTPUT_KIND)kind));
        GC.KeepAlive(stage);
    }

    public Stage GetOutput(TextGenerationPipelinePhase phase, out uint outputIndex, out TextGenerationOutputKind kind)
    {
        IWinMLStage* stage = null;
        uint index = 0;
        WINML_TEXT_GENERATION_OUTPUT_KIND outputKind = default;
        Check(Raw->GetOutput((WINML_TEXT_GENERATION_PIPELINE_PHASE)phase, &stage, &index, &outputKind));
        outputIndex = index;
        kind = (TextGenerationOutputKind)outputKind;
        return new Stage(Required(stage));
    }

    // The stateful stage whose sequence, such as a KV cache, the session manages.
    public void SetStateOwnerStage(Stage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        Check(Raw->SetStateOwnerStage(stage.Raw));
        GC.KeepAlive(stage);
    }

    public Stage GetStateOwnerStage()
    {
        IWinMLStage* stage = null;
        Check(Raw->GetStateOwnerStage(&stage));
        return new Stage(Required(stage));
    }

    // The target that creates the token tensors the session binds.
    public void SetTokenTarget(ExecutionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Check(Raw->SetTokenTarget(target.Raw));
        GC.KeepAlive(target);
    }

    public ExecutionTarget GetTokenTarget()
    {
        IWinMLExecutionTarget* target = null;
        Check(Raw->GetTokenTarget(&target));
        return new ExecutionTarget(Required(target));
    }

    // Binds a tensor that the application owns to a stage output, so the
    // session writes that output into it.
    public void AddCallerOutputBinding(TextGenerationPipelinePhase phase, Stage stage, uint outputIndex, Tensor tensor)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(tensor);
        Check(Raw->AddCallerOutputBinding((WINML_TEXT_GENERATION_PIPELINE_PHASE)phase, stage.Raw, outputIndex, tensor.Raw));
        GC.KeepAlive(stage);
        GC.KeepAlive(tensor);
    }

    public uint GetCallerOutputBindingCount(TextGenerationPipelinePhase phase)
    {
        uint count;
        Check(Raw->GetCallerOutputBindingCount((WINML_TEXT_GENERATION_PIPELINE_PHASE)phase, &count));
        return count;
    }

    // Dispose the returned binding to release its stage and tensor.
    public TextGenerationOutputBinding GetCallerOutputBinding(TextGenerationPipelinePhase phase, uint bindingIndex)
    {
        IWinMLStage* stage = null;
        uint outputIndex = 0;
        IWinMLTensor* tensor = null;
        HRESULT hr = Raw->GetCallerOutputBinding((WINML_TEXT_GENERATION_PIPELINE_PHASE)phase, bindingIndex, &stage, &outputIndex, &tensor);
        if (hr.Value < 0 || stage == null || tensor == null)
        {
            Release(stage);
            Release(tensor);
            Check(hr);
            throw new InvalidOperationException("The Runtime returned no object.");
        }

        return new TextGenerationOutputBinding(new Stage(stage), outputIndex, new Tensor(tensor));
    }

    // Checks that the pipelines and endpoints form a composition a session can run.
    public void Validate()
    {
        Check(Raw->Validate());
    }
}

// A caller-owned tensor bound to a stage output.
public sealed class TextGenerationOutputBinding : IDisposable
{
    internal TextGenerationOutputBinding(Stage stage, uint outputIndex, Tensor tensor)
    {
        Stage = stage;
        OutputIndex = outputIndex;
        Tensor = tensor;
    }

    public Stage Stage { get; }

    public uint OutputIndex { get; }

    public Tensor Tensor { get; }

    public void Dispose()
    {
        Tensor.Dispose();
        Stage.Dispose();
    }
}

// Binds a loaded model to a configuration in place of a pipeline.
public sealed unsafe class ModelTextGenerationConfiguration : WinMLObject, IWinMLInterface<ModelTextGenerationConfiguration>
{
    private ModelTextGenerationConfiguration(IWinMLModelTextGenerationConfiguration* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLModelTextGenerationConfiguration.IID_Guid;

    static ModelTextGenerationConfiguration IWinMLInterface<ModelTextGenerationConfiguration>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLModelTextGenerationConfiguration*)pointer);

    // Plans model for target and creates the model's own tokenizer from its
    // files. Throws NotSupportedException for a model or target that Windows ML
    // cannot run for text generation.
    public void SetModel(ExecutionTarget target, Model model, TextGenerationModelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(model);
        WINML_TEXT_GENERATION_MODEL_OPTIONS native = default;
        fixed (char* directory = options?.CompiledProgramDirectory)
        {
            FillModelOptions(options, directory, ref native);
            Check(((IWinMLModelTextGenerationConfiguration*)ThisPtr)->SetModel(target.Raw, model.Raw, &native));
        }

        GC.KeepAlive(target);
        GC.KeepAlive(model);
        GC.KeepAlive(options?.DraftModel);
    }

    // Describes a decoder that the application exported as one or more
    // models, and plans it for target as SetModel does.
    public void SetComposition(ExecutionTarget target, TextGenerationComposedModel description, TextGenerationModelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(description);
        uint[] indices = new uint[(CountStatePairs(description.Model) + CountStatePairs(description.Prefill) + CountStatePairs(description.Decode)) * 2];
        WINML_TEXT_GENERATION_COMPOSED_MODEL native = new()
        {
            kind = (WINML_TEXT_GENERATION_COMPOSITION_KIND)description.Kind,
            tokenEmbedding = description.TokenEmbedding is null ? null : description.TokenEmbedding.Raw,
            head = description.Head is null ? null : description.Head.Raw,
        };
        if (description.DecodeInputMode is TextGenerationDecodeInputMode decodeInputMode)
        {
            native.presentFields |= WINML_TEXT_GENERATION_COMPOSED_MODEL_FIELDS.WINML_TEXT_GENERATION_COMPOSED_MODEL_FIELDS_DECODE_INPUT_MODE;
            native.decodeInputMode = (WINML_TEXT_GENERATION_DECODE_INPUT_MODE)decodeInputMode;
        }

        if (description.LogicalCapacity is ulong logicalCapacity)
        {
            native.presentFields |= WINML_TEXT_GENERATION_COMPOSED_MODEL_FIELDS.WINML_TEXT_GENERATION_COMPOSED_MODEL_FIELDS_LOGICAL_CAPACITY;
            native.logicalCapacity = logicalCapacity;
        }

        WINML_TEXT_GENERATION_MODEL_OPTIONS nativeOptions = default;
        fixed (uint* indexPointer = indices)
        fixed (char* directory = options?.CompiledProgramDirectory)
        {
            int next = 0;
            native.model = ToNative(description.Model, indexPointer, ref next);
            native.prefill = ToNative(description.Prefill, indexPointer, ref next);
            native.decode = ToNative(description.Decode, indexPointer, ref next);
            FillModelOptions(options, directory, ref nativeOptions);
            Check(((IWinMLModelTextGenerationConfiguration*)ThisPtr)->SetComposition(target.Raw, &native, &nativeOptions));
        }

        GC.KeepAlive(target);
        GC.KeepAlive(description);
        GC.KeepAlive(options?.DraftModel);
    }

    private static int CountStatePairs(TextGenerationComposedStage? stage) => stage?.StatePairs?.Count ?? 0;

    // Splits a stage's state pairs into the input and output index arrays the
    // native stage points at, in indices from next on.
    private static WINML_TEXT_GENERATION_COMPOSED_STAGE ToNative(TextGenerationComposedStage? stage, uint* indices, ref int next)
    {
        if (stage is null)
        {
            return default;
        }

        IReadOnlyList<TextGenerationStatePair> pairs = stage.StatePairs ?? [];
        uint* inputs = indices + next;
        uint* outputs = inputs + pairs.Count;
        for (int i = 0; i < pairs.Count; ++i)
        {
            inputs[i] = pairs[i].InputIndex;
            outputs[i] = pairs[i].OutputIndex;
        }

        next += pairs.Count * 2;
        return new WINML_TEXT_GENERATION_COMPOSED_STAGE
        {
            model = stage.Model is null ? null : stage.Model.Raw,
            tokenInputIndex = stage.TokenInputIndex,
            outputIndex = stage.OutputIndex,
            outputKind = (WINML_TEXT_GENERATION_OUTPUT_KIND)stage.OutputKind,
            statePairCount = (uint)pairs.Count,
            stateInputIndices = pairs.Count == 0 ? null : inputs,
            stateOutputIndices = pairs.Count == 0 ? null : outputs,
        };
    }

    // directory is options.CompiledProgramDirectory, pinned by the caller.
    private static void FillModelOptions(TextGenerationModelOptions? options, char* directory, ref WINML_TEXT_GENERATION_MODEL_OPTIONS native)
    {
        if (options?.CompiledProgramDirectory is not null)
        {
            native.presentFields |= WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS.WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_DIRECTORY;
            native.compiledProgramDirectory = new PCWSTR(directory);
        }

        if (options?.CompiledProgramStorage is CompiledProgramStorage storage)
        {
            native.presentFields |= WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS.WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_STORAGE;
            native.compiledProgramStorage = (WINML_COMPILED_PROGRAM_STORAGE)storage;
        }

        if (options?.DraftModel is Model draftModel)
        {
            native.presentFields |= WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS.WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS_DRAFT_MODEL;
            native.draftModel = draftModel.Raw;
        }

        if (options?.PrefillProfile is TextGenerationPrefillProfile prefillProfile)
        {
            native.presentFields |= WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS.WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS_PREFILL_PROFILE;
            native.prefillProfile = (WINML_TEXT_GENERATION_PREFILL_PROFILE)prefillProfile;
        }
    }
}

public sealed unsafe class TextGenerationSpeculativeConfiguration : WinMLObject, IWinMLInterface<TextGenerationSpeculativeConfiguration>
{
    private TextGenerationSpeculativeConfiguration(IWinMLTextGenerationSpeculativeConfiguration* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTextGenerationSpeculativeConfiguration.IID_Guid;

    static TextGenerationSpeculativeConfiguration IWinMLInterface<TextGenerationSpeculativeConfiguration>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLTextGenerationSpeculativeConfiguration*)pointer);

    public void SetSpeculativeOptions(TextGenerationSpeculativeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        WINML_TEXT_GENERATION_SPECULATIVE_OPTIONS native = new()
        {
            method = (WINML_TEXT_GENERATION_SPECULATIVE_METHOD)options.Method,
            draftTokenCount = options.DraftTokenCount,
        };
        Check(((IWinMLTextGenerationSpeculativeConfiguration*)ThisPtr)->SetSpeculativeOptions(&native));
    }

    public TextGenerationSpeculativeOptions GetSpeculativeOptions()
    {
        WINML_TEXT_GENERATION_SPECULATIVE_OPTIONS native;
        Check(((IWinMLTextGenerationSpeculativeConfiguration*)ThisPtr)->GetSpeculativeOptions(&native));
        return new TextGenerationSpeculativeOptions
        {
            Method = (TextGenerationSpeculativeMethod)native.method,
            DraftTokenCount = native.draftTokenCount,
        };
    }

    // The separate draft model pipeline that DraftModel speculation uses: its
    // token input, logits output, and state owner. Null passes null for
    // every object.
    public void SetDraftPipeline(TextGenerationDraftPipeline? draft)
    {
        Check(((IWinMLTextGenerationSpeculativeConfiguration*)ThisPtr)->SetDraftPipeline(
            draft is null ? null : draft.Pipeline.Raw,
            draft is null ? null : draft.InputStage.Raw,
            draft?.InputIndex ?? 0,
            draft is null ? null : draft.OutputStage.Raw,
            draft?.OutputIndex ?? 0,
            draft is null ? null : draft.StateOwnerStage.Raw));
        GC.KeepAlive(draft);
    }

    // Throws InvalidOperationException (E_NOT_VALID_STATE) when no draft
    // pipeline is set. Dispose the returned description to release its
    // pipeline and stages.
    public TextGenerationDraftPipeline GetDraftPipeline()
    {
        IWinMLPipeline* pipeline = null;
        IWinMLStage* inputStage = null;
        uint inputIndex = 0;
        IWinMLStage* outputStage = null;
        uint outputIndex = 0;
        IWinMLStage* stateOwnerStage = null;
        HRESULT hr = ((IWinMLTextGenerationSpeculativeConfiguration*)ThisPtr)->GetDraftPipeline(
            &pipeline, &inputStage, &inputIndex, &outputStage, &outputIndex, &stateOwnerStage);
        if (hr.Value < 0 || pipeline == null || inputStage == null || outputStage == null || stateOwnerStage == null)
        {
            Release(pipeline);
            Release(inputStage);
            Release(outputStage);
            Release(stateOwnerStage);
            Check(hr);
            throw new InvalidOperationException("The Runtime returned no object.");
        }

        return new TextGenerationDraftPipeline(
            new Pipeline(pipeline), new Stage(inputStage), inputIndex, new Stage(outputStage), outputIndex, new Stage(stateOwnerStage))
        {
            OwnsObjects = true,
        };
    }
}

// A draft model pipeline for DraftModel speculation.
public sealed class TextGenerationDraftPipeline : IDisposable
{
    public TextGenerationDraftPipeline(Pipeline pipeline, Stage inputStage, uint inputIndex, Stage outputStage, uint outputIndex, Stage stateOwnerStage)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(inputStage);
        ArgumentNullException.ThrowIfNull(outputStage);
        ArgumentNullException.ThrowIfNull(stateOwnerStage);
        Pipeline = pipeline;
        InputStage = inputStage;
        InputIndex = inputIndex;
        OutputStage = outputStage;
        OutputIndex = outputIndex;
        StateOwnerStage = stateOwnerStage;
    }

    public Pipeline Pipeline { get; }

    public Stage InputStage { get; }

    public uint InputIndex { get; }

    public Stage OutputStage { get; }

    public uint OutputIndex { get; }

    public Stage StateOwnerStage { get; }

    // Set on a description that GetDraftPipeline returned, which owns its objects.
    internal bool OwnsObjects { get; init; }

    // Disposes the pipeline and stages of a description that GetDraftPipeline
    // returned. Does nothing for one the application created, whose objects
    // stay the application's.
    public void Dispose()
    {
        if (!OwnsObjects)
        {
            return;
        }

        StateOwnerStage.Dispose();
        OutputStage.Dispose();
        InputStage.Dispose();
        Pipeline.Dispose();
    }
}

public sealed unsafe class TextGenerationTask : WinMLObject, IWinMLInterface<TextGenerationTask>
{
    internal TextGenerationTask(IWinMLTextGenerationTask* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTextGenerationTask.IID_Guid;

    static TextGenerationTask IWinMLInterface<TextGenerationTask>.CreateFromOwnedPointer(nint pointer) => new((IWinMLTextGenerationTask*)pointer);

    // Creates a session bound to the calling thread. The session consumes the
    // configuration. The first session loads the engine, so a missing engine
    // payload fails here with FileNotFoundException or DllNotFoundException.
    public TextGenerationSession CreateSession(TextGenerationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IWinMLTextGenerationSession* session = null;
        Check(((IWinMLTextGenerationTask*)ThisPtr)->CreateSession(configuration.Raw, &session));
        GC.KeepAlive(configuration);
        return new TextGenerationSession(Required(session));
    }

    public Tokenizer GetTokenizer()
    {
        IWinMLTokenizer* tokenizer = null;
        Check(((IWinMLTextGenerationTask*)ThisPtr)->GetTokenizer(&tokenizer));
        return new Tokenizer(Required(tokenizer));
    }

    // The tokens that end generation: the tokenizer's end-of-sequence tokens,
    // or the explicit list the task was created with.
    public uint[] GetDefaultEosTokenIds()
    {
        uint count = 0;
        uint* tokens = null;
        Check(((IWinMLTextGenerationTask*)ThisPtr)->GetDefaultEosTokenIds(&count, &tokens));
        return Tokenizer.TakeTokenIds(tokens, count);
    }
}

// Generates text. Call it only on the thread that created it; Dispose closes it.
public sealed unsafe class TextGenerationSession : WinMLObject, IWinMLInterface<TextGenerationSession>
{
    private readonly bool _borrowed;
    private bool _closed;

    internal TextGenerationSession(IWinMLTextGenerationSession* pointer)
        : base(pointer)
    {
    }

    // A session that another object owns, such as the one a chat session
    // wraps. Dispose releases the reference without closing the session.
    internal TextGenerationSession(IWinMLTextGenerationSession* pointer, bool borrowed)
        : base(pointer)
    {
        _borrowed = borrowed;
    }

    public static Guid InterfaceId => IWinMLTextGenerationSession.IID_Guid;

    static TextGenerationSession IWinMLInterface<TextGenerationSession>.CreateFromOwnedPointer(nint pointer) => new((IWinMLTextGenerationSession*)pointer);

    private IWinMLTextGenerationSession* Self => (IWinMLTextGenerationSession*)ThisPtr;

    internal IWinMLTextGenerationSession* Raw => Self;

    public TextGenerationCapabilities Capabilities
    {
        get
        {
            WINML_TEXT_GENERATION_CAPABILITIES capabilities;
            Check(Self->GetCapabilities(&capabilities));
            return new TextGenerationCapabilities(
                capabilities.supportsContinuation,
                capabilities.supportsConstraints,
                capabilities.supportsSegmentedPrompts,
                capabilities.supportsEmbeddingSegments,
                capabilities.supportsConstrainedContinuation,
                capabilities.supportsConstrainedEmbeddingSegments,
                (TextGenerationExecutionProfile)capabilities.executionProfile,
                (TextGenerationOutputKind)capabilities.outputKind,
                TextGenerationSequenceState.FromNative(capabilities.sequenceState));
        }
    }

    public TextGenerationSessionState State
    {
        get
        {
            WINML_TEXT_GENERATION_SESSION_STATE state;
            Check(Self->GetState(&state));
            return (TextGenerationSessionState)state;
        }
    }

    // The Runtime the session's configuration was created with.
    public WinMLRuntime GetRuntime()
    {
        IWinMLRuntime* runtime = null;
        Check(Self->GetRuntime(&runtime));
        return new WinMLRuntime(Required(runtime));
    }

    // The configuration the session consumed. Its getters report the
    // pipelines and stages the session runs.
    public TextGenerationConfiguration GetConfiguration()
    {
        IWinMLTextGenerationConfiguration* configuration = null;
        Check(Self->GetConfiguration(&configuration));
        return new TextGenerationConfiguration(Required(configuration));
    }

    // Appends tokenIds to the sequence the session retains and generates from
    // it. Check Capabilities.SupportsContinuation, and compare
    // TextGenerationSequenceDisclosure.GetRetainedTokenIds with the new prompt.
    public TextGenerationPullStream ContinueTokens(ReadOnlySpan<uint> tokenIds, TextGenerationOptions options, CancellationSource? cancellation = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        IWinMLTextGenerationPullStream* stream = null;
        fixed (uint* tokens = tokenIds)
        {
            Check(Self->ContinueTokens((uint)tokenIds.Length, tokenIds.IsEmpty ? null : tokens, options.Raw, cancellation is null ? null : cancellation.Raw, &stream));
        }

        GC.KeepAlive(options);
        GC.KeepAlive(cancellation);
        return new TextGenerationPullStream(Required(stream));
    }

    // Generates from a prompt made of token and embedding segments, such as
    // text around an encoded image. Check Capabilities.SupportsSegmentedPrompts.
    public TextGenerationPullStream GenerateSegments(IReadOnlyList<GenerationSegment> segments, TextGenerationOptions options, CancellationSource? cancellation = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        using NativeGenerationSegments native = new(segments);
        IWinMLTextGenerationPullStream* stream = null;
        Check(Self->GenerateSegments(native.Count, native.Pointer, options.Raw, cancellation is null ? null : cancellation.Raw, &stream));
        GC.KeepAlive(options);
        GC.KeepAlive(cancellation);
        return new TextGenerationPullStream(Required(stream));
    }

    // Loads a multimodal projector for the session's decoder. A null target
    // uses the session's target.
    public MediaEncoder CreateMediaEncoderFromFile(string encoderPath, ExecutionTarget? target = null)
    {
        ArgumentNullException.ThrowIfNull(encoderPath);
        IWinMLMediaEncoder* encoder = null;
        fixed (char* path = encoderPath)
        {
            Check(Self->CreateMediaEncoderFromFile(new PCWSTR(path), target is null ? null : target.Raw, &encoder));
        }

        GC.KeepAlive(target);
        return new MediaEncoder(Required(encoder));
    }

    // Discards the retained sequence, after a completed or failed generation.
    public void Reset()
    {
        Check(Self->Reset());
    }

    // Generates from a prompt, which the session encodes without a chat template.
    public TextGenerationPullStream GenerateText(string prompt, TextGenerationOptions options, CancellationSource? cancellation = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);
        IWinMLTextGenerationPullStream* stream = null;
        fixed (char* text = prompt)
        {
            Check(Self->GenerateText(new PCWSTR(text), options.Raw, cancellation is null ? null : cancellation.Raw, &stream));
        }

        GC.KeepAlive(options);
        GC.KeepAlive(cancellation);
        return new TextGenerationPullStream(Required(stream));
    }

    // Replaces the session's sequence with tokenIds and generates from it.
    public TextGenerationPullStream GenerateTokens(ReadOnlySpan<uint> tokenIds, TextGenerationOptions options, CancellationSource? cancellation = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        IWinMLTextGenerationPullStream* stream = null;
        fixed (uint* tokens = tokenIds)
        {
            Check(Self->GenerateTokens((uint)tokenIds.Length, tokens, options.Raw, cancellation is null ? null : cancellation.Raw, &stream));
        }

        GC.KeepAlive(options);
        GC.KeepAlive(cancellation);
        return new TextGenerationPullStream(Required(stream));
    }

    // Releases the session's execution resources.
    public void Close()
    {
        if (_borrowed)
        {
            throw new InvalidOperationException("Another object owns this one; close that object instead.");
        }

        if (!_closed)
        {
            Check(Self->Close());
            _closed = true;
        }
    }

    // Dispose closes a session that was not closed, unless another object
    // owns it. A failure to close then is not reported, as the C++ samples'
    // closers do not report it.
    private protected override void OnDisposing()
    {
        if (!_closed && !_borrowed)
        {
            _closed = true;
            _ = Self->Close();
        }
    }
}

// One generated token and its text, borrowed from the stream until the next read.
public readonly ref struct TextGenerationFragment
{
    internal TextGenerationFragment(uint tokenId, ReadOnlySpan<char> text)
    {
        TokenId = tokenId;
        Text = text;
    }

    public uint TokenId { get; }

    public ReadOnlySpan<char> Text { get; }
}

// The tokens of one generation. Read it on the session's thread; Dispose closes it.
public sealed unsafe class TextGenerationPullStream : WinMLObject, IWinMLInterface<TextGenerationPullStream>
{
    private readonly bool _borrowed;
    private bool _closed;

    internal TextGenerationPullStream(IWinMLTextGenerationPullStream* pointer)
        : base(pointer)
    {
    }

    // A stream that another stream drives, such as the one under a chat
    // completion stream. Dispose releases the reference without closing it.
    internal TextGenerationPullStream(IWinMLTextGenerationPullStream* pointer, bool borrowed)
        : base(pointer)
    {
        _borrowed = borrowed;
    }

    public static Guid InterfaceId => IWinMLTextGenerationPullStream.IID_Guid;

    static TextGenerationPullStream IWinMLInterface<TextGenerationPullStream>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLTextGenerationPullStream*)pointer);

    private IWinMLTextGenerationPullStream* Self => (IWinMLTextGenerationPullStream*)ThisPtr;

    // Reads the next token without allocating. Returns false when generation
    // has completed; GetResult then reports the outcome. The text is borrowed
    // from the stream until the next read. Throws InvalidDataException for a
    // status this wrapper does not know.
    public bool TryReadNext(out uint tokenId, out ReadOnlySpan<char> text)
    {
        WINML_TEXT_GENERATION_READ_STATUS status;
        uint token = 0;
        PCWSTR fragment;
        Check(Self->ReadNext(&status, &token, &fragment));
        tokenId = token;
        if (status == WINML_TEXT_GENERATION_READ_STATUS.WINML_TEXT_GENERATION_READ_STATUS_COMPLETED)
        {
            text = default;
            return false;
        }

        if (status != WINML_TEXT_GENERATION_READ_STATUS.WINML_TEXT_GENERATION_READ_STATUS_UPDATE)
        {
            throw WinMLException.InvalidData($"Unexpected read status {(int)status}.");
        }

        text = fragment.Value == null
            ? throw WinMLException.InvalidData("The stream returned an update without text.")
            : MemoryMarshal.CreateReadOnlySpanFromNullTerminated(fragment.Value);
        return true;
    }

    // An allocation-free enumerator for foreach on the session's thread.
    public Enumerator GetEnumerator() => new(this);

    public TextGenerationResult GetResult()
    {
        IWinMLTextGenerationResult* result = null;
        Check(Self->GetResult(&result));
        return new TextGenerationResult(Required(result));
    }

    // Releases the stream's execution resources.
    public void Close()
    {
        if (_borrowed)
        {
            throw new InvalidOperationException("Another object owns this one; close that object instead.");
        }

        if (!_closed)
        {
            Check(Self->Close());
            _closed = true;
        }
    }

    private protected override void OnDisposing()
    {
        if (!_closed && !_borrowed)
        {
            _closed = true;
            _ = Self->Close();
        }
    }

    public ref struct Enumerator
    {
        private readonly TextGenerationPullStream _stream;

        internal Enumerator(TextGenerationPullStream stream)
        {
            _stream = stream;
            Current = default;
        }

        public TextGenerationFragment Current { get; private set; }

        public bool MoveNext()
        {
            if (_stream.TryReadNext(out uint tokenId, out ReadOnlySpan<char> text))
            {
                Current = new TextGenerationFragment(tokenId, text);
                return true;
            }

            Current = default;
            return false;
        }
    }
}

// The outcome of one generation.
public sealed unsafe class TextGenerationResult : WinMLObject, IWinMLInterface<TextGenerationResult>
{
    internal TextGenerationResult(IWinMLTextGenerationResult* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTextGenerationResult.IID_Guid;

    static TextGenerationResult IWinMLInterface<TextGenerationResult>.CreateFromOwnedPointer(nint pointer) => new((IWinMLTextGenerationResult*)pointer);

    private IWinMLTextGenerationResult* Self => (IWinMLTextGenerationResult*)ThisPtr;

    // Why generation stopped.
    public TextGenerationFinishReason FinishReason
    {
        get
        {
            WINML_TEXT_GENERATION_FINISH_REASON reason;
            Check(Self->GetFinishReason(&reason));
            return (TextGenerationFinishReason)reason;
        }
    }

    // The failure that ended generation, or zero. A canceled generation reports
    // HRESULT_FROM_WIN32(ERROR_CANCELLED).
    public int ErrorCode
    {
        get
        {
            HRESULT error;
            Check(Self->GetErrorCode(&error));
            return error.Value;
        }
    }

    // The generated text: every streamed fragment, in order.
    public string Text
    {
        get
        {
            PCWSTR text;
            Check(Self->GetText(&text));
            string result = text.Value == null ? string.Empty : new string(text.Value);
            GC.KeepAlive(this);
            return result;
        }
    }

    public uint PromptTokenCount
    {
        get
        {
            uint count;
            Check(Self->GetPromptTokenCount(&count));
            return count;
        }
    }

    public uint[] GetTokenIds()
    {
        uint count = 0;
        uint* tokens = null;
        Check(Self->GetTokenIds(&count, &tokens));
        return Tokenizer.TakeTokenIds(tokens, count);
    }
}

// The speculative decoding counts of a result. Query a TextGenerationResult for it.
public sealed unsafe class TextGenerationSpeculativeResult : WinMLObject, IWinMLInterface<TextGenerationSpeculativeResult>
{
    private TextGenerationSpeculativeResult(IWinMLTextGenerationSpeculativeResult* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTextGenerationSpeculativeResult.IID_Guid;

    static TextGenerationSpeculativeResult IWinMLInterface<TextGenerationSpeculativeResult>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLTextGenerationSpeculativeResult*)pointer);

    public TextGenerationSpeculativeStatistics SpeculativeStatistics
    {
        get
        {
            WINML_TEXT_GENERATION_SPECULATIVE_STATISTICS statistics;
            Check(((IWinMLTextGenerationSpeculativeResult*)ThisPtr)->GetSpeculativeStatistics(&statistics));
            return new TextGenerationSpeculativeStatistics(
                (TextGenerationSpeculativeMethod)statistics.method,
                statistics.verificationStepCount,
                statistics.draftTokenCount,
                statistics.acceptedDraftTokenCount);
        }
    }
}

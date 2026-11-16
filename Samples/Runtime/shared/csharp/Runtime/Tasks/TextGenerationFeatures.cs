// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. The optional text generation interfaces
// that an application queries for: the sequence a session retains,
// constrained generation and continuation, segmented prompts, speculative
// method support, watermarking, and the Runtime identity of a configuration.
// Session views and streams belong to the session's thread.

using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Microsoft.Windows.AI.MachineLearning.Runtime.Tasks;

namespace WinMLSamples.Runtime.Tasks;

public enum TextGenerationSequenceDisclosureStatus
{
    // The session holds no sequence.
    None = 0,

    // The session holds a sequence that ContinueTokens can extend.
    Retained = 1,

    // The session holds state that cannot be continued; reset or replace it.
    Unusable = 2,
}

// The sequence a session holds. RetainedTokenCount tokens can be read back
// with TextGenerationSequenceDisclosure.GetRetainedTokenIds.
public readonly record struct TextGenerationSequenceState(
    TextGenerationSequenceDisclosureStatus Disclosure,
    TextGenerationSessionState SessionState,
    ulong EvaluatedTokenCount,
    uint RetainedTokenCount)
{
    internal static TextGenerationSequenceState FromNative(in WINML_TEXT_GENERATION_SEQUENCE_STATE state)
    {
        return new TextGenerationSequenceState(
            (TextGenerationSequenceDisclosureStatus)state.disclosure,
            (TextGenerationSessionState)state.sessionState,
            state.evaluatedTokenCount,
            state.retainedTokenCount);
    }
}

public enum TextGenerationCompositionKind
{
    // One model with a token input and a logits or token output.
    Unified = 0,

    // A token embedding model, a decoder, and a head.
    EmbeddingDecoder = 1,

    // Separate prefill and decode models.
    PrefillDecode = 2,
}

// A state input that a stage feeds from one of its own outputs on the next step.
public readonly record struct TextGenerationStatePair(uint InputIndex, uint OutputIndex);

// One model of a composition and its endpoints.
public sealed class TextGenerationComposedStage
{
    public Model? Model { get; init; }

    public uint TokenInputIndex { get; init; }

    public uint OutputIndex { get; init; }

    public TextGenerationOutputKind OutputKind { get; init; }

    public IReadOnlyList<TextGenerationStatePair>? StatePairs { get; init; }
}

// A decoder that the application exported as one or more models. Set the
// stages that Kind uses: Model for Unified and EmbeddingDecoder, Prefill and
// Decode for PrefillDecode.
public sealed class TextGenerationComposedModel
{
    public TextGenerationCompositionKind Kind { get; init; }

    public TextGenerationComposedStage? Model { get; init; }

    public Model? TokenEmbedding { get; init; }

    public Model? Head { get; init; }

    public TextGenerationComposedStage? Prefill { get; init; }

    public TextGenerationComposedStage? Decode { get; init; }

    public TextGenerationDecodeInputMode? DecodeInputMode { get; init; }

    public ulong? LogicalCapacity { get; init; }
}

// One part of a segmented prompt: token IDs, or embedding rows such as an
// encoded image with the layout its encoder reported.
public sealed class GenerationSegment
{
    private GenerationSegment(ReadOnlyMemory<uint> tokenIds, Tensor? embeddings, SequenceSegmentLayout? layout)
    {
        TokenIds = tokenIds;
        Embeddings = embeddings;
        Layout = layout;
    }

    public ReadOnlyMemory<uint> TokenIds { get; }

    public Tensor? Embeddings { get; }

    public SequenceSegmentLayout? Layout { get; }

    public static GenerationSegment FromTokens(ReadOnlyMemory<uint> tokenIds) => new(tokenIds, null, null);

    public static GenerationSegment FromEmbeddings(Tensor embeddings, SequenceSegmentLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        return new GenerationSegment(default, embeddings, layout);
    }

    public static GenerationSegment FromEncoding(MediaEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return new GenerationSegment(default, encoding.Embeddings, encoding.Layout);
    }
}

// Pins the segments' token arrays and lays out the native segment array for
// one call. The segments' objects stay referenced by the managed list.
internal sealed unsafe class NativeGenerationSegments : IDisposable
{
    private readonly IReadOnlyList<GenerationSegment> _segments;
    private readonly MemoryHandle[] _pins;
    private WINML_GENERATION_SEGMENT* _native;

    public NativeGenerationSegments(IReadOnlyList<GenerationSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        _segments = segments;
        _pins = new MemoryHandle[segments.Count];
        _native = segments.Count == 0 ? null : (WINML_GENERATION_SEGMENT*)NativeMemory.AllocZeroed((nuint)segments.Count, (nuint)sizeof(WINML_GENERATION_SEGMENT));
        try
        {
            for (int i = 0; i < segments.Count; ++i)
            {
                GenerationSegment segment = segments[i] ?? throw new ArgumentException("A generation segment is null.", nameof(segments));
                if (segment.Embeddings is Tensor embeddings)
                {
                    _native[i].kind = WINML_GENERATION_SEGMENT_KIND.WINML_GENERATION_SEGMENT_KIND_EMBEDDINGS;
                    _native[i].embeddings = embeddings.Raw;
                    _native[i].layout = segment.Layout is null ? null : segment.Layout.Raw;
                }
                else
                {
                    _pins[i] = segment.TokenIds.Pin();
                    _native[i].kind = WINML_GENERATION_SEGMENT_KIND.WINML_GENERATION_SEGMENT_KIND_TOKENS;
                    _native[i].tokenCount = (uint)segment.TokenIds.Length;
                    _native[i].tokenIds = (uint*)_pins[i].Pointer;
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public uint Count => (uint)_segments.Count;

    public WINML_GENERATION_SEGMENT* Pointer => _native;

    public void Dispose()
    {
        foreach (MemoryHandle pin in _pins)
        {
            pin.Dispose();
        }

        NativeMemory.Free(_native);
        _native = null;
        GC.KeepAlive(_segments);
    }
}

// The sequence a session retains, for deciding whether a new prompt can
// continue it. Query a TextGenerationSession for it, on the session's thread.
public sealed unsafe class TextGenerationSequenceDisclosure : WinMLObject, IWinMLInterface<TextGenerationSequenceDisclosure>
{
    private TextGenerationSequenceDisclosure(IWinMLTextGenerationSequenceDisclosure* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTextGenerationSequenceDisclosure.IID_Guid;

    static TextGenerationSequenceDisclosure IWinMLInterface<TextGenerationSequenceDisclosure>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLTextGenerationSequenceDisclosure*)pointer);

    public TextGenerationSequenceState SequenceState
    {
        get
        {
            WINML_TEXT_GENERATION_SEQUENCE_STATE state;
            Check(((IWinMLTextGenerationSequenceDisclosure*)ThisPtr)->GetSequenceState(&state));
            return TextGenerationSequenceState.FromNative(state);
        }
    }

    // Compare token IDs, not text, to decide whether a prompt extends the sequence.
    public uint[] GetRetainedTokenIds()
    {
        uint count = 0;
        uint* tokens = null;
        Check(((IWinMLTextGenerationSequenceDisclosure*)ThisPtr)->GetRetainedTokenIds(&count, &tokens));
        return Tokenizer.TakeTokenIds(tokens, count);
    }
}

// Starts generation under a token constraint. Query a TextGenerationSession
// for it when Capabilities.SupportsConstraints is set, on the session's thread.
public sealed unsafe class TextGenerationConstraintSession : WinMLObject, IWinMLInterface<TextGenerationConstraintSession>
{
    private TextGenerationConstraintSession(IWinMLTextGenerationConstraintSession* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTextGenerationConstraintSession.IID_Guid;

    static TextGenerationConstraintSession IWinMLInterface<TextGenerationConstraintSession>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLTextGenerationConstraintSession*)pointer);

    private IWinMLTextGenerationConstraintSession* Self => (IWinMLTextGenerationConstraintSession*)ThisPtr;

    // Replaces the session's sequence with tokenIds and generates only tokens
    // that constraint allows. The constraint must come from the session's tokenizer.
    public TextGenerationPullStream GenerateTokensWithConstraint(
        ReadOnlySpan<uint> tokenIds,
        TokenConstraint constraint,
        TextGenerationOptions options,
        CancellationSource? cancellation = null)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        ArgumentNullException.ThrowIfNull(options);
        IWinMLTextGenerationPullStream* stream = null;
        fixed (uint* tokens = tokenIds)
        {
            Check(Self->GenerateTokensWithConstraint(
                (uint)tokenIds.Length, tokens, constraint.Raw, options.Raw, cancellation is null ? null : cancellation.Raw, &stream));
        }

        GC.KeepAlive(constraint);
        GC.KeepAlive(options);
        GC.KeepAlive(cancellation);
        return new TextGenerationPullStream(Required(stream));
    }

    public TextGenerationPullStream GenerateSegmentsWithConstraint(
        IReadOnlyList<GenerationSegment> segments,
        TokenConstraint constraint,
        TextGenerationOptions options,
        CancellationSource? cancellation = null)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        ArgumentNullException.ThrowIfNull(options);
        using NativeGenerationSegments native = new(segments);
        IWinMLTextGenerationPullStream* stream = null;
        Check(Self->GenerateSegmentsWithConstraint(
            native.Count, native.Pointer, constraint.Raw, options.Raw, cancellation is null ? null : cancellation.Raw, &stream));
        GC.KeepAlive(constraint);
        GC.KeepAlive(options);
        GC.KeepAlive(cancellation);
        return new TextGenerationPullStream(Required(stream));
    }
}

// Continues a retained sequence under a token constraint. Query a
// TextGenerationSession for it when Capabilities.SupportsConstrainedContinuation is set.
public sealed unsafe class TextGenerationConstraintContinuation : WinMLObject, IWinMLInterface<TextGenerationConstraintContinuation>
{
    private TextGenerationConstraintContinuation(IWinMLTextGenerationConstraintContinuation* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTextGenerationConstraintContinuation.IID_Guid;

    static TextGenerationConstraintContinuation IWinMLInterface<TextGenerationConstraintContinuation>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLTextGenerationConstraintContinuation*)pointer);

    public TextGenerationPullStream ContinueTokensWithConstraint(
        ReadOnlySpan<uint> tokenIds,
        TokenConstraint constraint,
        TextGenerationOptions options,
        CancellationSource? cancellation = null)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        ArgumentNullException.ThrowIfNull(options);
        IWinMLTextGenerationPullStream* stream = null;
        fixed (uint* tokens = tokenIds)
        {
            Check(((IWinMLTextGenerationConstraintContinuation*)ThisPtr)->ContinueTokensWithConstraint(
                (uint)tokenIds.Length, tokenIds.IsEmpty ? null : tokens, constraint.Raw, options.Raw, cancellation is null ? null : cancellation.Raw, &stream));
        }

        GC.KeepAlive(constraint);
        GC.KeepAlive(options);
        GC.KeepAlive(cancellation);
        return new TextGenerationPullStream(Required(stream));
    }
}

public enum TextGenerationDraftSource
{
    None = 0,
    ModelLayers = 1,
    CompanionModel = 2,
    DraftModel = 3,
    Sequence = 4,
}

// What a speculative method does for the bound model and draft model.
public readonly record struct TextGenerationSpeculativeMethodSupport(
    bool Supported,
    TextGenerationDraftSource DraftSource,
    uint DefaultDraftTokenCount,
    uint MaxDraftTokenCount,
    bool ModelDraftLayersPresent);

// Reports speculative method support without creating a session. Query a
// configuration for it after ModelTextGenerationConfiguration.SetModel.
public sealed unsafe class TextGenerationSpeculativeSupport : WinMLObject, IWinMLInterface<TextGenerationSpeculativeSupport>
{
    private TextGenerationSpeculativeSupport(IWinMLTextGenerationSpeculativeSupport* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTextGenerationSpeculativeSupport.IID_Guid;

    static TextGenerationSpeculativeSupport IWinMLInterface<TextGenerationSpeculativeSupport>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLTextGenerationSpeculativeSupport*)pointer);

    // Throws NotSupportedException when the route reports support only through
    // its sessions, and InvalidOperationException when no model is bound.
    public TextGenerationSpeculativeMethodSupport GetSpeculativeMethodSupport(TextGenerationSpeculativeMethod method)
    {
        WINML_TEXT_GENERATION_SPECULATIVE_METHOD_SUPPORT support;
        Check(((IWinMLTextGenerationSpeculativeSupport*)ThisPtr)->GetSpeculativeMethodSupport((WINML_TEXT_GENERATION_SPECULATIVE_METHOD)method, &support));
        return new TextGenerationSpeculativeMethodSupport(
            support.supported,
            (TextGenerationDraftSource)support.draftSource,
            support.defaultDraftTokenCount,
            support.maxDraftTokenCount,
            support.modelDraftLayersPresent);
    }
}

public enum TextGenerationWatermarkAlgorithm
{
    None = 0,
    Gumbel = 1,
    DualKeyGumbel = 2,
    SynthId = 3,
}

public enum TextGenerationWatermarkPseudorandomFunction
{
    Philox4x32x10V1 = 0,
}

public enum TextGenerationWatermarkEnforcement
{
    // Generate without the watermark where it cannot apply, and count those tokens.
    BestEffort = 0,

    // Fail a request that the watermark cannot apply to.
    Required = 1,
}

// The generated tokens whose repeated context a token's watermark checks.
public enum TextGenerationWatermarkContextScope
{
    SingleTurn = 0,
    None = 1,
    All = 2,
}

// The key and parameters of a watermark. A detector needs the same values.
// Zero values select the Runtime's defaults where it has them.
public sealed class TextGenerationWatermarkOptions
{
    public TextGenerationWatermarkAlgorithm Algorithm { get; init; }

    public TextGenerationWatermarkPseudorandomFunction PseudorandomFunction { get; init; }

    public TextGenerationWatermarkEnforcement Enforcement { get; init; }

    public TextGenerationWatermarkContextScope ContextScope { get; init; }

    public ulong Key { get; init; }

    // Identifies the key in statistics; the key itself is never reported.
    public uint KeyId { get; init; }

    // How many preceding tokens seed each token's pseudorandom draw.
    public uint ContextWidth { get; init; }

    // For DualKeyGumbel, the probability of drawing with the derived key.
    public float Alpha { get; init; }

    public uint MaxContextHistory { get; init; }

    // For SynthId, the number of tournament layers.
    public uint Depth { get; init; }

    internal WINML_TEXT_GENERATION_WATERMARK_OPTIONS ToNative()
    {
        return new WINML_TEXT_GENERATION_WATERMARK_OPTIONS
        {
            algorithm = (WINML_TEXT_GENERATION_WATERMARK_ALGORITHM)Algorithm,
            prf = (WINML_TEXT_GENERATION_WATERMARK_PRF)PseudorandomFunction,
            enforcement = (WINML_TEXT_GENERATION_WATERMARK_ENFORCEMENT)Enforcement,
            contextScope = (WINML_TEXT_GENERATION_WATERMARK_CONTEXT_SCOPE)ContextScope,
            key = Key,
            keyId = KeyId,
            contextWidth = ContextWidth,
            alpha = Alpha,
            maxContextHistory = MaxContextHistory,
            depth = Depth,
        };
    }

    internal static TextGenerationWatermarkOptions FromNative(in WINML_TEXT_GENERATION_WATERMARK_OPTIONS native)
    {
        return new TextGenerationWatermarkOptions
        {
            Algorithm = (TextGenerationWatermarkAlgorithm)native.algorithm,
            PseudorandomFunction = (TextGenerationWatermarkPseudorandomFunction)native.prf,
            Enforcement = (TextGenerationWatermarkEnforcement)native.enforcement,
            ContextScope = (TextGenerationWatermarkContextScope)native.contextScope,
            Key = native.key,
            KeyId = native.keyId,
            ContextWidth = native.contextWidth,
            Alpha = native.alpha,
            MaxContextHistory = native.maxContextHistory,
            Depth = native.depth,
        };
    }
}

// How many of a generation's tokens carry the watermark, and why the others don't.
public readonly record struct TextGenerationWatermarkStatistics(
    TextGenerationWatermarkAlgorithm Algorithm,
    TextGenerationWatermarkPseudorandomFunction PseudorandomFunction,
    TextGenerationWatermarkContextScope ContextScope,
    uint KeyId,
    uint ContextWidth,
    float Alpha,
    uint Depth,
    uint GeneratedTokenCount,
    uint WatermarkedTokenCount,
    uint SecondaryKeyTokenCount,
    uint RepeatedContextTokenCount,
    uint PartialContextTokenCount,
    uint GreedyTokenCount,
    uint OtherUnwatermarkedTokenCount);

// A small PValue, such as below 1e-3, indicates the watermark.
public readonly record struct TextGenerationWatermarkDetection(uint TokenCount, uint ScoredTokenCount, double Score, double PValue);

// Embeds a watermark in the tokens that sessions sample. Query a configuration
// for it and set options before CreateSession.
public sealed unsafe class TextGenerationWatermarkConfiguration : WinMLObject, IWinMLInterface<TextGenerationWatermarkConfiguration>
{
    private TextGenerationWatermarkConfiguration(IWinMLTextGenerationWatermarkConfiguration* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTextGenerationWatermarkConfiguration.IID_Guid;

    static TextGenerationWatermarkConfiguration IWinMLInterface<TextGenerationWatermarkConfiguration>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLTextGenerationWatermarkConfiguration*)pointer);

    public void SetWatermarkOptions(TextGenerationWatermarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        WINML_TEXT_GENERATION_WATERMARK_OPTIONS native = options.ToNative();
        Check(((IWinMLTextGenerationWatermarkConfiguration*)ThisPtr)->SetWatermarkOptions(&native));
    }

    public TextGenerationWatermarkOptions GetWatermarkOptions()
    {
        WINML_TEXT_GENERATION_WATERMARK_OPTIONS native;
        Check(((IWinMLTextGenerationWatermarkConfiguration*)ThisPtr)->GetWatermarkOptions(&native));
        return TextGenerationWatermarkOptions.FromNative(native);
    }
}

// The watermark counts of a result. Query a TextGenerationResult for it.
public sealed unsafe class TextGenerationWatermarkResult : WinMLObject, IWinMLInterface<TextGenerationWatermarkResult>
{
    private TextGenerationWatermarkResult(IWinMLTextGenerationWatermarkResult* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTextGenerationWatermarkResult.IID_Guid;

    static TextGenerationWatermarkResult IWinMLInterface<TextGenerationWatermarkResult>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLTextGenerationWatermarkResult*)pointer);

    public TextGenerationWatermarkStatistics WatermarkStatistics
    {
        get
        {
            WINML_TEXT_GENERATION_WATERMARK_STATISTICS s;
            Check(((IWinMLTextGenerationWatermarkResult*)ThisPtr)->GetWatermarkStatistics(&s));
            return new TextGenerationWatermarkStatistics(
                (TextGenerationWatermarkAlgorithm)s.algorithm,
                (TextGenerationWatermarkPseudorandomFunction)s.prf,
                (TextGenerationWatermarkContextScope)s.contextScope,
                s.keyId,
                s.contextWidth,
                s.alpha,
                s.depth,
                s.generatedTokenCount,
                s.watermarkedTokenCount,
                s.secondaryKeyTokenCount,
                s.repeatedContextTokenCount,
                s.partialContextTokenCount,
                s.greedyTokenCount,
                s.otherUnwatermarkedTokenCount);
        }
    }
}

// Scores token IDs for a watermark without the model or the prompt. Query the
// TextGenerationTaskFactory for it.
public sealed unsafe class TextGenerationWatermarkDetector : WinMLObject, IWinMLInterface<TextGenerationWatermarkDetector>
{
    private TextGenerationWatermarkDetector(IWinMLTextGenerationWatermarkDetector* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTextGenerationWatermarkDetector.IID_Guid;

    static TextGenerationWatermarkDetector IWinMLInterface<TextGenerationWatermarkDetector>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLTextGenerationWatermarkDetector*)pointer);

    // options must be the ones the generation used.
    public TextGenerationWatermarkDetection DetectWatermark(TextGenerationWatermarkOptions options, ReadOnlySpan<uint> tokenIds)
    {
        ArgumentNullException.ThrowIfNull(options);
        WINML_TEXT_GENERATION_WATERMARK_OPTIONS native = options.ToNative();
        WINML_TEXT_GENERATION_WATERMARK_DETECTION detection;
        fixed (uint* tokens = tokenIds)
        {
            Check(((IWinMLTextGenerationWatermarkDetector*)ThisPtr)->DetectWatermark(
                &native, (uint)tokenIds.Length, tokenIds.IsEmpty ? null : tokens, &detection));
        }

        return new TextGenerationWatermarkDetection(detection.tokenCount, detection.scoredTokenCount, detection.score, detection.pValue);
    }
}

// The Runtime that a Task object belongs to. Query a text generation
// configuration or a Task session for it; sessions also have GetRuntime.
public sealed unsafe class TaskRuntimeIdentity : WinMLObject, IWinMLInterface<TaskRuntimeIdentity>
{
    private TaskRuntimeIdentity(IWinMLTaskRuntimeIdentity* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTaskRuntimeIdentity.IID_Guid;

    static TaskRuntimeIdentity IWinMLInterface<TaskRuntimeIdentity>.CreateFromOwnedPointer(nint pointer) => new((IWinMLTaskRuntimeIdentity*)pointer);

    public WinMLRuntime GetRuntime()
    {
        IWinMLRuntime* runtime = null;
        Check(((IWinMLTaskRuntimeIdentity*)ThisPtr)->GetRuntime(&runtime));
        return new WinMLRuntime(Required(runtime));
    }
}

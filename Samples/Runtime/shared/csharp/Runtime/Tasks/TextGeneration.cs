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
}

public sealed class TextGenerationSpeculativeOptions
{
    public TextGenerationSpeculativeMethod Method { get; init; }

    // Zero lets the Runtime choose.
    public uint DraftTokenCount { get; init; }
}

// The Task API root object.
public sealed unsafe class WinMLTasks : WinMLObject, IWinMLInterface<WinMLTasks>
{
    private TextGenerationTaskFactory? _textGeneration;

    private WinMLTasks(IWinMLTasks* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTasks.IID_Guid;

    static WinMLTasks IWinMLInterface<WinMLTasks>.CreateFromOwnedPointer(nint pointer) => new((IWinMLTasks*)pointer);

    // The factory for text generation configurations, options, and tasks.
    public TextGenerationTaskFactory TextGeneration => GetCachedInterface(ref _textGeneration);

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

    private protected override void OnDisposing()
    {
        DisposeCached(_textGeneration);
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

            Check(((IWinMLModelTextGenerationConfiguration*)ThisPtr)->SetModel(target.Raw, model.Raw, &native));
        }

        GC.KeepAlive(target);
        GC.KeepAlive(model);
        GC.KeepAlive(options?.DraftModel);
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
}

// Generates text. Call it only on the thread that created it; Dispose closes it.
public sealed unsafe class TextGenerationSession : WinMLObject, IWinMLInterface<TextGenerationSession>
{
    private bool _closed;

    internal TextGenerationSession(IWinMLTextGenerationSession* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTextGenerationSession.IID_Guid;

    static TextGenerationSession IWinMLInterface<TextGenerationSession>.CreateFromOwnedPointer(nint pointer) => new((IWinMLTextGenerationSession*)pointer);

    private IWinMLTextGenerationSession* Self => (IWinMLTextGenerationSession*)ThisPtr;

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
        if (!_closed)
        {
            Check(Self->Close());
            _closed = true;
        }
    }

    // Dispose closes a session that was not closed. A failure to close then is
    // not reported, as the C++ samples' closers do not report it.
    private protected override void OnDisposing()
    {
        if (!_closed)
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
    private bool _closed;

    internal TextGenerationPullStream(IWinMLTextGenerationPullStream* pointer)
        : base(pointer)
    {
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
        if (!_closed)
        {
            Check(Self->Close());
            _closed = true;
        }
    }

    private protected override void OnDisposing()
    {
        if (!_closed)
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

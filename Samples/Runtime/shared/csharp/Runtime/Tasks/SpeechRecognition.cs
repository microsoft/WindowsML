// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. The automatic speech recognition Task
// API: a task turns a waveform into a transcript, from a published speech
// model or from Whisper encoder and decoder pipelines that the application
// built. A session, its streams, and its live input are bound to the thread
// that created the session; SpeechRecognizer owns one on a SessionThread for
// use from any thread.

using System.Runtime.InteropServices;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Microsoft.Windows.AI.MachineLearning.Runtime.Tasks;
using Windows.Win32.Foundation;
using Win32 = Windows.Win32.PInvoke;

namespace WinMLSamples.Runtime.Tasks;

public enum SpeechRecognitionSessionState
{
    Empty = 0,
    Ready = 1,
    Active = 2,
    ResetRequired = 3,
    Closed = 4,
}

public enum SpeechRecognitionFinishReason
{
    InputComplete = 0,
    Capacity = 1,
    Canceled = 2,
    Error = 3,
}

public enum SpeechRecognitionReadStatus
{
    // Recognized text, never revised. The updates concatenate into the transcript.
    Update = 0,

    // A live stream needs more audio before it can continue.
    NeedInput = 1,

    Completed = 2,

    // Provisional text that follows the recognized text and replaces the
    // previous prediction; empty withdraws it. Only with PredictionsEnabled.
    Prediction = 3,
}

public enum SpeechRecognitionMode
{
    // Writes the speech as text in the language spoken.
    Transcribe = 0,

    // Writes the speech as English text.
    Translate = 1,
}

// The valid span of a waveform tensor: normalized mono samples at SampleRate.
public readonly record struct SpeechRecognitionWaveformMetadata(uint SampleRate, uint ChannelCount, ulong ValidSampleCount);

// How SetModel prepares a speech model. Unset properties keep the Runtime defaults.
public sealed class SpeechRecognitionModelOptions
{
    // A directory that keeps the programs sessions compile.
    public string? CompiledProgramDirectory { get; init; }

    public CompiledProgramStorage? CompiledProgramStorage { get; init; }
}

// The encoder and decoder pipelines of an encoder-decoder composition, such
// as Whisper. The preprocessing pair is optional; set both or neither.
public sealed class EncoderDecoderSpeechRecognitionBindings : IDisposable
{
    public required Pipeline EncoderPipeline { get; init; }

    public required Stage EncoderStage { get; init; }

    public required Pipeline DecoderPipeline { get; init; }

    public required Stage DecoderStage { get; init; }

    // A target that creates the tensors the task binds between the pipelines.
    public required ExecutionTarget TensorTarget { get; init; }

    public Pipeline? PreprocessingPipeline { get; init; }

    public Stage? PreprocessingStage { get; init; }

    // Copies the encoder output into the decoder input instead of binding it directly.
    public bool ForceEncoderOutputCopy { get; init; }

    // Set on bindings that GetBindings returned, which own their objects.
    internal bool OwnsObjects { get; init; }

    // Disposes every object of bindings that GetBindings returned. Does
    // nothing for bindings the application created, whose objects stay the
    // application's.
    public void Dispose()
    {
        if (!OwnsObjects)
        {
            return;
        }

        PreprocessingStage?.Dispose();
        PreprocessingPipeline?.Dispose();
        TensorTarget.Dispose();
        DecoderStage.Dispose();
        DecoderPipeline.Dispose();
        EncoderStage.Dispose();
        EncoderPipeline.Dispose();
    }
}

// The result of writing one live chunk. LifetimeWatermark, when present, is
// the highest chunk ID whose waveform the session no longer reads, so the
// application can reuse its tensors.
public readonly record struct SpeechRecognitionChunkResult(bool Accepted, ulong? LifetimeWatermark);

public sealed unsafe class AutomaticSpeechRecognitionTaskFactory : WinMLObject, IWinMLInterface<AutomaticSpeechRecognitionTaskFactory>
{
    private AutomaticSpeechRecognitionTaskFactory(IWinMLAutomaticSpeechRecognitionTaskFactory* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLAutomaticSpeechRecognitionTaskFactory.IID_Guid;

    static AutomaticSpeechRecognitionTaskFactory IWinMLInterface<AutomaticSpeechRecognitionTaskFactory>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLAutomaticSpeechRecognitionTaskFactory*)pointer);

    // tokenizer converts the recognized tokens to text, such as the one a
    // speech model folder's tokenizer.json describes.
    public AutomaticSpeechRecognitionTask CreateAutomaticSpeechRecognitionTask(Tokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        IWinMLAutomaticSpeechRecognitionTask* task = null;
        Check(((IWinMLAutomaticSpeechRecognitionTaskFactory*)ThisPtr)->CreateAutomaticSpeechRecognitionTask(tokenizer.Raw, &task));
        GC.KeepAlive(tokenizer);
        return new AutomaticSpeechRecognitionTask(Required(task));
    }
}

public sealed unsafe class AutomaticSpeechRecognitionTask : WinMLObject, IWinMLInterface<AutomaticSpeechRecognitionTask>
{
    internal AutomaticSpeechRecognitionTask(IWinMLAutomaticSpeechRecognitionTask* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLAutomaticSpeechRecognitionTask.IID_Guid;

    static AutomaticSpeechRecognitionTask IWinMLInterface<AutomaticSpeechRecognitionTask>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLAutomaticSpeechRecognitionTask*)pointer);

    // Configure it with ModelSpeechRecognitionConfiguration.SetModel or
    // EncoderDecoderSpeechRecognitionConfiguration.SetBindings.
    public SpeechRecognitionConfiguration CreateConfiguration()
    {
        IWinMLAutomaticSpeechRecognitionConfiguration* configuration = null;
        Check(((IWinMLAutomaticSpeechRecognitionTask*)ThisPtr)->CreateConfiguration(&configuration));
        return new SpeechRecognitionConfiguration(Required(configuration));
    }

    // Creates a session bound to the calling thread. The session consumes the
    // configuration. Throws NotSupportedException when the model or
    // composition cannot honor the configuration's language or mode.
    public SpeechRecognitionSession CreateSession(SpeechRecognitionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IWinMLAutomaticSpeechRecognitionSession* session = null;
        Check(((IWinMLAutomaticSpeechRecognitionTask*)ThisPtr)->CreateSession(configuration.Raw, &session));
        GC.KeepAlive(configuration);
        return new SpeechRecognitionSession(Required(session));
    }
}

// The language, mode, and prediction settings of a session, and the model or
// pipelines it runs. Query it for ModelSpeechRecognitionConfiguration or
// EncoderDecoderSpeechRecognitionConfiguration to bind those.
public sealed unsafe class SpeechRecognitionConfiguration : WinMLObject, IWinMLInterface<SpeechRecognitionConfiguration>
{
    internal SpeechRecognitionConfiguration(IWinMLAutomaticSpeechRecognitionConfiguration* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLAutomaticSpeechRecognitionConfiguration.IID_Guid;

    static SpeechRecognitionConfiguration IWinMLInterface<SpeechRecognitionConfiguration>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLAutomaticSpeechRecognitionConfiguration*)pointer);

    internal IWinMLAutomaticSpeechRecognitionConfiguration* Raw => (IWinMLAutomaticSpeechRecognitionConfiguration*)ThisPtr;

    // The language spoken, as a BCP 47 tag such as "en". Null or empty
    // restores the default: identify the language, or the model's default.
    // Getting returns an empty string for the default.
    public string? Language
    {
        get
        {
            PWSTR language;
            Check(Raw->GetLanguage(&language));
            try
            {
                return language.Value == null ? string.Empty : new string(language.Value);
            }
            finally
            {
                Win32.CoTaskMemFree(language.Value);
            }
        }

        set
        {
            fixed (char* language = value)
            {
                Check(Raw->SetLanguage(new PCWSTR(language)));
            }
        }
    }

    public SpeechRecognitionMode Mode
    {
        get
        {
            WINML_AUTOMATIC_SPEECH_RECOGNITION_MODE mode;
            Check(Raw->GetMode(&mode));
            return (SpeechRecognitionMode)mode;
        }

        set => Check(Raw->SetMode((WINML_AUTOMATIC_SPEECH_RECOGNITION_MODE)value));
    }

    // Opts in to SpeechRecognitionReadStatus.Prediction updates.
    public bool PredictionsEnabled
    {
        get
        {
            BOOL enabled;
            Check(Raw->GetPredictionsEnabled(&enabled));
            return enabled;
        }

        set => Check(Raw->SetPredictionsEnabled(value));
    }

    // Checks the bound model or composition; does not check language or mode.
    public void Validate()
    {
        Check(Raw->Validate());
    }
}

// Binds a published speech model, such as a Whisper checkpoint folder.
public sealed unsafe class ModelSpeechRecognitionConfiguration : WinMLObject, IWinMLInterface<ModelSpeechRecognitionConfiguration>
{
    private ModelSpeechRecognitionConfiguration(IWinMLModelAutomaticSpeechRecognitionConfiguration* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLModelAutomaticSpeechRecognitionConfiguration.IID_Guid;

    static ModelSpeechRecognitionConfiguration IWinMLInterface<ModelSpeechRecognitionConfiguration>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLModelAutomaticSpeechRecognitionConfiguration*)pointer);

    // Reads the model's files and runs every model computation on target.
    // Throws NotSupportedException for a model Windows ML cannot run for
    // speech recognition.
    public void SetModel(ExecutionTarget target, Model model, SpeechRecognitionModelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(model);
        WINML_AUTOMATIC_SPEECH_RECOGNITION_MODEL_OPTIONS native = default;
        fixed (char* directory = options?.CompiledProgramDirectory)
        {
            if (options?.CompiledProgramDirectory is not null)
            {
                native.presentFields |= WINML_AUTOMATIC_SPEECH_RECOGNITION_MODEL_OPTION_FIELDS.WINML_AUTOMATIC_SPEECH_RECOGNITION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_DIRECTORY;
                native.compiledProgramDirectory = new PCWSTR(directory);
            }

            if (options?.CompiledProgramStorage is CompiledProgramStorage storage)
            {
                native.presentFields |= WINML_AUTOMATIC_SPEECH_RECOGNITION_MODEL_OPTION_FIELDS.WINML_AUTOMATIC_SPEECH_RECOGNITION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_STORAGE;
                native.compiledProgramStorage = (WINML_COMPILED_PROGRAM_STORAGE)storage;
            }

            Check(((IWinMLModelAutomaticSpeechRecognitionConfiguration*)ThisPtr)->SetModel(
                target.Raw, model.Raw, options is null ? null : &native));
        }

        GC.KeepAlive(target);
        GC.KeepAlive(model);
    }
}

// Binds encoder and decoder pipelines that the application built.
public sealed unsafe class EncoderDecoderSpeechRecognitionConfiguration : WinMLObject, IWinMLInterface<EncoderDecoderSpeechRecognitionConfiguration>
{
    private EncoderDecoderSpeechRecognitionConfiguration(IWinMLEncoderDecoderAutomaticSpeechRecognitionConfiguration* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLEncoderDecoderAutomaticSpeechRecognitionConfiguration.IID_Guid;

    static EncoderDecoderSpeechRecognitionConfiguration IWinMLInterface<EncoderDecoderSpeechRecognitionConfiguration>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLEncoderDecoderAutomaticSpeechRecognitionConfiguration*)pointer);

    private IWinMLEncoderDecoderAutomaticSpeechRecognitionConfiguration* Self => (IWinMLEncoderDecoderAutomaticSpeechRecognitionConfiguration*)ThisPtr;

    // Replaces the bindings. A failed call keeps the previous ones.
    public void SetBindings(EncoderDecoderSpeechRecognitionBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if ((bindings.PreprocessingPipeline is null) != (bindings.PreprocessingStage is null))
        {
            throw new ArgumentException("Set both PreprocessingPipeline and PreprocessingStage, or neither.", nameof(bindings));
        }

        WINML_ENCODER_DECODER_AUTOMATIC_SPEECH_RECOGNITION_BINDINGS native = new()
        {
            encoderPipeline = bindings.EncoderPipeline.Raw,
            encoderStage = bindings.EncoderStage.Raw,
            decoderPipeline = bindings.DecoderPipeline.Raw,
            decoderStage = bindings.DecoderStage.Raw,
            tensorTarget = bindings.TensorTarget.Raw,
            preprocessingPipeline = bindings.PreprocessingPipeline is null ? null : bindings.PreprocessingPipeline.Raw,
            preprocessingStage = bindings.PreprocessingStage is null ? null : bindings.PreprocessingStage.Raw,
            forceEncoderOutputCopy = bindings.ForceEncoderOutputCopy,
        };
        Check(Self->SetBindings(&native));
        GC.KeepAlive(bindings);
    }

    // Returns new references to the bound objects; dispose the result.
    public EncoderDecoderSpeechRecognitionBindings GetBindings()
    {
        WINML_ENCODER_DECODER_AUTOMATIC_SPEECH_RECOGNITION_BINDINGS native = default;
        HRESULT hr = Self->GetBindings(&native);
        if (hr.Value < 0 || native.encoderPipeline == null || native.encoderStage == null || native.decoderPipeline == null ||
            native.decoderStage == null || native.tensorTarget == null)
        {
            Release(native.encoderPipeline);
            Release(native.encoderStage);
            Release(native.decoderPipeline);
            Release(native.decoderStage);
            Release(native.tensorTarget);
            Release(native.preprocessingPipeline);
            Release(native.preprocessingStage);
            Check(hr);
            throw new InvalidOperationException("The configuration has no bindings.");
        }

        return new EncoderDecoderSpeechRecognitionBindings
        {
            EncoderPipeline = new Pipeline(native.encoderPipeline),
            EncoderStage = new Stage(native.encoderStage),
            DecoderPipeline = new Pipeline(native.decoderPipeline),
            DecoderStage = new Stage(native.decoderStage),
            TensorTarget = new ExecutionTarget(native.tensorTarget),
            PreprocessingPipeline = native.preprocessingPipeline == null ? null : new Pipeline(native.preprocessingPipeline),
            PreprocessingStage = native.preprocessingStage == null ? null : new Stage(native.preprocessingStage),
            ForceEncoderOutputCopy = native.forceEncoderOutputCopy,
            OwnsObjects = true,
        };
    }
}

// Transcribes waveforms. Call it only on the thread that created it; Dispose closes it.
public sealed unsafe class SpeechRecognitionSession : WinMLObject, IWinMLInterface<SpeechRecognitionSession>
{
    private bool _closed;

    internal SpeechRecognitionSession(IWinMLAutomaticSpeechRecognitionSession* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLAutomaticSpeechRecognitionSession.IID_Guid;

    static SpeechRecognitionSession IWinMLInterface<SpeechRecognitionSession>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLAutomaticSpeechRecognitionSession*)pointer);

    private IWinMLAutomaticSpeechRecognitionSession* Self => (IWinMLAutomaticSpeechRecognitionSession*)ThisPtr;

    public SpeechRecognitionSessionState State
    {
        get
        {
            WINML_AUTOMATIC_SPEECH_RECOGNITION_SESSION_STATE state;
            Check(Self->GetState(&state));
            return (SpeechRecognitionSessionState)state;
        }
    }

    public WinMLRuntime GetRuntime()
    {
        IWinMLRuntime* runtime = null;
        Check(Self->GetRuntime(&runtime));
        return new WinMLRuntime(Required(runtime));
    }

    public SpeechRecognitionConfiguration GetConfiguration()
    {
        IWinMLAutomaticSpeechRecognitionConfiguration* configuration = null;
        Check(Self->GetConfiguration(&configuration));
        return new SpeechRecognitionConfiguration(Required(configuration));
    }

    // Transcribes the valid span of a waveform tensor of shape [samples] or
    // [1, samples]. The session copies the span before it runs.
    public SpeechRecognitionPullStream TranscribeWaveform(Tensor waveform, SpeechRecognitionWaveformMetadata metadata, CancellationSource? cancellation = null)
    {
        ArgumentNullException.ThrowIfNull(waveform);
        WINML_AUTOMATIC_SPEECH_RECOGNITION_WAVEFORM_METADATA native = ToNative(metadata);
        IWinMLAutomaticSpeechRecognitionPullStream* stream = null;
        Check(Self->TranscribeWaveform(waveform.Raw, &native, cancellation is null ? null : cancellation.Raw, &stream));
        GC.KeepAlive(waveform);
        GC.KeepAlive(cancellation);
        return new SpeechRecognitionPullStream(Required(stream));
    }

    // Discards the state of a completed or failed operation.
    public void Reset()
    {
        Check(Self->Reset());
    }

    // Fails while an operation is active.
    public void Close()
    {
        if (!_closed)
        {
            Check(Self->Close());
            _closed = true;
        }
    }

    internal static WINML_AUTOMATIC_SPEECH_RECOGNITION_WAVEFORM_METADATA ToNative(SpeechRecognitionWaveformMetadata metadata)
    {
        return new WINML_AUTOMATIC_SPEECH_RECOGNITION_WAVEFORM_METADATA
        {
            sampleRate = metadata.SampleRate,
            channelCount = metadata.ChannelCount,
            validSampleCount = metadata.ValidSampleCount,
        };
    }

    private protected override void OnDisposing()
    {
        if (!_closed)
        {
            _closed = true;
            _ = Self->Close();
        }
    }
}

// Transcribes encoder input that the application computed, such as Whisper
// log-mel features, in place of a waveform. Query an encoder-decoder
// SpeechRecognitionSession for it, on the session's thread.
public sealed unsafe class SpeechRecognitionEncoderInput : WinMLObject, IWinMLInterface<SpeechRecognitionEncoderInput>
{
    private SpeechRecognitionEncoderInput(IWinMLAutomaticSpeechRecognitionEncoderInput* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLAutomaticSpeechRecognitionEncoderInput.IID_Guid;

    static SpeechRecognitionEncoderInput IWinMLInterface<SpeechRecognitionEncoderInput>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLAutomaticSpeechRecognitionEncoderInput*)pointer);

    // For Whisper, FLOAT32 [1, 80, 3000] log-mel features.
    public SpeechRecognitionPullStream TranscribeEncoderInput(Tensor encoderInput, CancellationSource? cancellation = null)
    {
        ArgumentNullException.ThrowIfNull(encoderInput);
        IWinMLAutomaticSpeechRecognitionPullStream* stream = null;
        Check(((IWinMLAutomaticSpeechRecognitionEncoderInput*)ThisPtr)->TranscribeEncoderInput(
            encoderInput.Raw, cancellation is null ? null : cancellation.Raw, &stream));
        GC.KeepAlive(encoderInput);
        GC.KeepAlive(cancellation);
        return new SpeechRecognitionPullStream(Required(stream));
    }
}

// Transcribes audio as the application writes it. Query a
// SpeechRecognitionSession for it, on the session's thread, when the model
// accepts live input.
public sealed unsafe class SpeechRecognitionLiveSession : WinMLObject, IWinMLInterface<SpeechRecognitionLiveSession>
{
    private SpeechRecognitionLiveSession(IWinMLAutomaticSpeechRecognitionLiveSession* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLAutomaticSpeechRecognitionLiveSession.IID_Guid;

    static SpeechRecognitionLiveSession IWinMLInterface<SpeechRecognitionLiveSession>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLAutomaticSpeechRecognitionLiveSession*)pointer);

    // Starts a live operation. metadata gives the sample rate, the channel
    // count, and a valid sample count, which must not be zero (zero throws
    // ArgumentException with E_INVALIDARG); pass the length of the audio the
    // application will write. Each chunk also gives its own valid sample
    // count. Write chunks to input, read the stream, and call
    // input.CompleteInput at the end of the audio.
    public SpeechRecognitionPullStream BeginLiveInput(
        SpeechRecognitionWaveformMetadata metadata,
        uint maximumPendingChunks,
        CancellationSource? cancellation,
        out SpeechRecognitionLiveInput input)
    {
        WINML_AUTOMATIC_SPEECH_RECOGNITION_WAVEFORM_METADATA native = SpeechRecognitionSession.ToNative(metadata);
        IWinMLAutomaticSpeechRecognitionPullStream* stream = null;
        IWinMLAutomaticSpeechRecognitionLiveInput* live = null;
        HRESULT hr = ((IWinMLAutomaticSpeechRecognitionLiveSession*)ThisPtr)->BeginLiveInput(
            &native, maximumPendingChunks, cancellation is null ? null : cancellation.Raw, &stream, &live);
        GC.KeepAlive(cancellation);
        if (hr.Value < 0 || stream == null || live == null)
        {
            Release(stream);
            Release(live);
            Check(hr);
            throw new InvalidOperationException("The Runtime returned no object.");
        }

        input = new SpeechRecognitionLiveInput(live);
        return new SpeechRecognitionPullStream(stream);
    }
}

// Accepts the audio of a live operation. Use it on the session's thread;
// Dispose closes it.
public sealed unsafe class SpeechRecognitionLiveInput : WinMLObject, IWinMLInterface<SpeechRecognitionLiveInput>
{
    private bool _closed;

    internal SpeechRecognitionLiveInput(IWinMLAutomaticSpeechRecognitionLiveInput* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLAutomaticSpeechRecognitionLiveInput.IID_Guid;

    static SpeechRecognitionLiveInput IWinMLInterface<SpeechRecognitionLiveInput>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLAutomaticSpeechRecognitionLiveInput*)pointer);

    private IWinMLAutomaticSpeechRecognitionLiveInput* Self => (IWinMLAutomaticSpeechRecognitionLiveInput*)ThisPtr;

    // Writes validSampleCount samples of waveform as chunk chunkId. Accepted
    // is false when the session already holds the maximum pending chunks;
    // read the stream, then write the chunk again.
    public SpeechRecognitionChunkResult WriteChunk(ulong chunkId, Tensor waveform, ulong validSampleCount)
    {
        ArgumentNullException.ThrowIfNull(waveform);
        BOOL accepted;
        BOOL hasWatermark;
        ulong watermark = 0;
        Check(Self->WriteChunk(chunkId, waveform.Raw, validSampleCount, &accepted, &hasWatermark, &watermark));
        GC.KeepAlive(waveform);
        return new SpeechRecognitionChunkResult(accepted, hasWatermark ? watermark : null);
    }

    // Ends the audio. The stream completes after it recognizes what remains.
    public void CompleteInput()
    {
        Check(Self->CompleteInput());
    }

    // Null until the session has released a chunk.
    public ulong? LifetimeWatermark
    {
        get
        {
            BOOL hasWatermark;
            ulong watermark = 0;
            Check(Self->GetLifetimeWatermark(&hasWatermark, &watermark));
            return hasWatermark ? watermark : null;
        }
    }

    // Requires CompleteInput or a completed stream first.
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
}

// One read from a speech recognition stream. Text is borrowed from the
// stream until the next read; it is empty for NeedInput.
public readonly ref struct SpeechRecognitionFragment
{
    internal SpeechRecognitionFragment(SpeechRecognitionReadStatus status, ReadOnlySpan<char> text)
    {
        Status = status;
        Text = text;
    }

    public SpeechRecognitionReadStatus Status { get; }

    public ReadOnlySpan<char> Text { get; }
}

// The updates of one operation. Read it on the session's thread; Dispose closes it.
public sealed unsafe class SpeechRecognitionPullStream : WinMLObject, IWinMLInterface<SpeechRecognitionPullStream>
{
    private bool _closed;

    internal SpeechRecognitionPullStream(IWinMLAutomaticSpeechRecognitionPullStream* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLAutomaticSpeechRecognitionPullStream.IID_Guid;

    static SpeechRecognitionPullStream IWinMLInterface<SpeechRecognitionPullStream>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLAutomaticSpeechRecognitionPullStream*)pointer);

    private IWinMLAutomaticSpeechRecognitionPullStream* Self => (IWinMLAutomaticSpeechRecognitionPullStream*)ThisPtr;

    // Reads the next update, need-input notice, or prediction without
    // allocating. Returns false when the operation has completed; GetResult
    // then reports the outcome. The text is borrowed until the next read.
    public bool TryReadNext(out SpeechRecognitionReadStatus status, out ReadOnlySpan<char> text)
    {
        WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS native;
        PCWSTR fragment;
        Check(Self->ReadNext(&native, &fragment));
        status = (SpeechRecognitionReadStatus)native;
        switch (status)
        {
            case SpeechRecognitionReadStatus.Completed:
                text = default;
                return false;
            case SpeechRecognitionReadStatus.NeedInput:
                text = default;
                return true;
            case SpeechRecognitionReadStatus.Update:
            case SpeechRecognitionReadStatus.Prediction:
                text = fragment.Value == null
                    ? throw WinMLException.InvalidData("The stream returned an update without text.")
                    : MemoryMarshal.CreateReadOnlySpanFromNullTerminated(fragment.Value);
                return true;
            default:
                throw WinMLException.InvalidData($"Unexpected read status {(int)native}.");
        }
    }

    // An allocation-free enumerator for foreach on the session's thread.
    public Enumerator GetEnumerator() => new(this);

    // Readable after the stream closes.
    public SpeechRecognitionResult GetResult()
    {
        IWinMLAutomaticSpeechRecognitionResult* result = null;
        Check(Self->GetResult(&result));
        return new SpeechRecognitionResult(Required(result));
    }

    // Cancels an operation that has not completed.
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
        private readonly SpeechRecognitionPullStream _stream;

        internal Enumerator(SpeechRecognitionPullStream stream)
        {
            _stream = stream;
            Current = default;
        }

        public SpeechRecognitionFragment Current { get; private set; }

        public bool MoveNext()
        {
            if (_stream.TryReadNext(out SpeechRecognitionReadStatus status, out ReadOnlySpan<char> text))
            {
                Current = new SpeechRecognitionFragment(status, text);
                return true;
            }

            Current = default;
            return false;
        }
    }
}

// The outcome of one operation.
public sealed unsafe class SpeechRecognitionResult : WinMLObject, IWinMLInterface<SpeechRecognitionResult>
{
    internal SpeechRecognitionResult(IWinMLAutomaticSpeechRecognitionResult* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLAutomaticSpeechRecognitionResult.IID_Guid;

    static SpeechRecognitionResult IWinMLInterface<SpeechRecognitionResult>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLAutomaticSpeechRecognitionResult*)pointer);

    private IWinMLAutomaticSpeechRecognitionResult* Self => (IWinMLAutomaticSpeechRecognitionResult*)ThisPtr;

    // Every Update fragment, in order.
    public string Transcript
    {
        get
        {
            PCWSTR text;
            Check(Self->GetTranscript(&text));
            string result = text.Value == null ? string.Empty : new string(text.Value);
            GC.KeepAlive(this);
            return result;
        }
    }

    public SpeechRecognitionFinishReason FinishReason
    {
        get
        {
            WINML_AUTOMATIC_SPEECH_RECOGNITION_FINISH_REASON reason;
            Check(Self->GetFinishReason(&reason));
            return (SpeechRecognitionFinishReason)reason;
        }
    }

    // The failure that ended the operation, or zero.
    public int ErrorCode
    {
        get
        {
            HRESULT error;
            Check(Self->GetErrorCode(&error));
            return error.Value;
        }
    }
}

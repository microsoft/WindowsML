// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. The chat completion Task API: a task
// composes a tokenizer's chat template with a text generation session. Each
// request is a whole conversation; the session formats it, reuses the tokens
// it already holds when the prompt starts with them, and streams content,
// reasoning, and tool-call events. A chat session and its streams belong to
// the thread of the text generation session they wrap; ChatGenerator owns one
// on a SessionThread for use from any thread.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Microsoft.Windows.AI.MachineLearning.Runtime.Tasks;
using Windows.Win32.Foundation;

namespace WinMLSamples.Runtime.Tasks;

public enum ChatCompletionFinishReason
{
    EosToken = 0,
    StopToken = 1,
    MaxTokens = 2,
    SequenceCapacity = 3,
    Canceled = 4,
    Error = 5,

    // A stop string that the chat template adds, such as an end-of-turn marker.
    AdditionalStop = 6,
}

public enum ConversationOutputEventType
{
    ContentDelta = 0,
    ReasoningDelta = 1,
    ToolCallStart = 2,
    ToolArgumentsDelta = 3,
    ToolCallEnd = 4,
    Completed = 5,
    Error = 6,
}

public enum ChatCompletionPromptCacheMode
{
    FullPrefill = 0,
    ReusedPrefix = 1,
    NotEvaluated = 2,
    Unknown = 3,
}

// How much of a prompt the session already held. CachedPromptTokenCount
// tokens were reused; PrefilledPromptTokenCount were evaluated.
public readonly record struct ChatCompletionPromptCacheUse(
    ChatCompletionPromptCacheMode Mode,
    uint PromptTokenCount,
    uint CachedPromptTokenCount,
    uint PrefilledPromptTokenCount);

public sealed unsafe class ChatCompletionTaskFactory : WinMLObject, IWinMLInterface<ChatCompletionTaskFactory>
{
    private ChatCompletionTaskFactory(IWinMLChatCompletionTaskFactory* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLChatCompletionTaskFactory.IID_Guid;

    static ChatCompletionTaskFactory IWinMLInterface<ChatCompletionTaskFactory>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLChatCompletionTaskFactory*)pointer);

    // tokenizer must be the one the text generation task uses, and must have a chat template.
    public ChatCompletionTask CreateChatCompletionTask(Tokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        IWinMLChatCompletionTask* task = null;
        Check(((IWinMLChatCompletionTaskFactory*)ThisPtr)->CreateChatCompletionTask(tokenizer.Raw, &task));
        GC.KeepAlive(tokenizer);
        return new ChatCompletionTask(Required(task));
    }
}

public sealed unsafe class ChatCompletionTask : WinMLObject, IWinMLInterface<ChatCompletionTask>
{
    internal ChatCompletionTask(IWinMLChatCompletionTask* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLChatCompletionTask.IID_Guid;

    static ChatCompletionTask IWinMLInterface<ChatCompletionTask>.CreateFromOwnedPointer(nint pointer) => new((IWinMLChatCompletionTask*)pointer);

    // Wraps a text generation session. Call it on the session's thread; the
    // chat session belongs to that thread. The caller still owns, and closes,
    // the text generation session.
    public ChatCompletionSession CreateSession(TextGenerationSession textGenerationSession)
    {
        ArgumentNullException.ThrowIfNull(textGenerationSession);
        IWinMLChatCompletionSession* session = null;
        Check(((IWinMLChatCompletionTask*)ThisPtr)->CreateSession(textGenerationSession.Raw, &session));
        GC.KeepAlive(textGenerationSession);
        return new ChatCompletionSession(Required(session));
    }

    // The tokenizer whose chat template the task applies.
    public Tokenizer GetTokenizer()
    {
        IWinMLTokenizer* tokenizer = null;
        Check(((IWinMLChatCompletionTask*)ThisPtr)->GetTokenizer(&tokenizer));
        return new Tokenizer(Required(tokenizer));
    }
}

// An image tensor for an image content part of a request: the part at
// ContentPartIndex of the message at MessageIndex.
public sealed record ChatMediaBinding(uint MessageIndex, uint ContentPartIndex, Tensor Tensor);

// Completes conversations. Call it only on its thread; Dispose closes it.
public sealed unsafe class ChatCompletionSession : WinMLObject, IWinMLInterface<ChatCompletionSession>
{
    private bool _closed;

    internal ChatCompletionSession(IWinMLChatCompletionSession* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLChatCompletionSession.IID_Guid;

    static ChatCompletionSession IWinMLInterface<ChatCompletionSession>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLChatCompletionSession*)pointer);

    private IWinMLChatCompletionSession* Self => (IWinMLChatCompletionSession*)ThisPtr;

    // Formats the request with the model's chat template and starts generating
    // the assistant's reply. Unsupported request fields, such as a JSON schema
    // on a session that cannot constrain its tokens, fail here.
    public ChatCompletionPullStream Generate(ConversationRequest request, TextGenerationOptions options, CancellationSource? cancellation = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        using NativeConversationRequest native = new(request);
        IWinMLChatCompletionPullStream* stream = null;
        Check(Self->Generate(native.Pointer, options.Raw, cancellation is null ? null : cancellation.Raw, &stream));
        GC.KeepAlive(options);
        GC.KeepAlive(cancellation);
        return new ChatCompletionPullStream(Required(stream));
    }

    // Like Generate, with an image tensor for each image content part of the
    // request. Bindings need a media encoder; see SetMediaEncoder.
    public ChatCompletionPullStream GenerateWithMedia(
        ConversationRequest request,
        IReadOnlyList<ChatMediaBinding> bindings,
        TextGenerationOptions options,
        CancellationSource? cancellation = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(options);
        using NativeConversationRequest native = new(request);
        WINML_CHAT_MEDIA_BINDING[] nativeBindings = new WINML_CHAT_MEDIA_BINDING[bindings.Count];
        for (int i = 0; i < bindings.Count; ++i)
        {
            ChatMediaBinding binding = bindings[i] ?? throw new ArgumentException("A media binding is null.", nameof(bindings));
            nativeBindings[i] = new WINML_CHAT_MEDIA_BINDING
            {
                messageIndex = binding.MessageIndex,
                contentPartIndex = binding.ContentPartIndex,
                tensor = (binding.Tensor ?? throw new ArgumentException("A media binding has no tensor.", nameof(bindings))).Raw,
            };
        }

        IWinMLChatCompletionPullStream* stream = null;
        fixed (WINML_CHAT_MEDIA_BINDING* bindingPointer = nativeBindings)
        {
            Check(Self->GenerateWithMedia(
                native.Pointer,
                (uint)nativeBindings.Length,
                nativeBindings.Length == 0 ? null : bindingPointer,
                options.Raw,
                cancellation is null ? null : cancellation.Raw,
                &stream));
        }

        GC.KeepAlive(bindings);
        GC.KeepAlive(options);
        GC.KeepAlive(cancellation);
        return new ChatCompletionPullStream(Required(stream));
    }

    // The encoder that turns bound images into embeddings for later requests,
    // such as one TextGenerationSession.CreateMediaEncoderFromFile created.
    // Null clears it.
    public void SetMediaEncoder(MediaEncoder? encoder)
    {
        Check(Self->SetMediaEncoder(encoder is null ? null : encoder.Raw));
        GC.KeepAlive(encoder);
    }

    // Null when no encoder is set.
    public MediaEncoder? GetMediaEncoder()
    {
        IWinMLMediaEncoder* encoder = null;
        Check(Self->GetMediaEncoder(&encoder));
        return encoder == null ? null : new MediaEncoder(encoder);
    }

    // The text generation session the chat session wraps. The caller that
    // created it still owns it: disposing the returned object releases a
    // reference without closing the session.
    public TextGenerationSession GetTextGenerationSession()
    {
        IWinMLTextGenerationSession* session = null;
        Check(Self->GetTextGenerationSession(&session));
        return new TextGenerationSession(Required(session), borrowed: true);
    }

    // Does not close the text generation session or outstanding streams.
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

// The events of one completion. Read it on the session's thread; Dispose closes it.
public sealed unsafe class ChatCompletionPullStream : WinMLObject, IWinMLInterface<ChatCompletionPullStream>
{
    private bool _closed;

    internal ChatCompletionPullStream(IWinMLChatCompletionPullStream* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLChatCompletionPullStream.IID_Guid;

    static ChatCompletionPullStream IWinMLInterface<ChatCompletionPullStream>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLChatCompletionPullStream*)pointer);

    private IWinMLChatCompletionPullStream* Self => (IWinMLChatCompletionPullStream*)ThisPtr;

    // Reads the next event. Returns false when the completion has finished;
    // GetResult then reports the outcome. Dispose the event.
    public bool TryReadNext([NotNullWhen(true)] out ConversationOutputEvent? outputEvent)
    {
        WINML_CHAT_COMPLETION_READ_STATUS status;
        IWinMLConversationOutputEvent* native = null;
        Check(Self->ReadNext(&status, &native));
        if (status == WINML_CHAT_COMPLETION_READ_STATUS.WINML_CHAT_COMPLETION_READ_STATUS_COMPLETED)
        {
            Release(native);
            outputEvent = null;
            return false;
        }

        if (status != WINML_CHAT_COMPLETION_READ_STATUS.WINML_CHAT_COMPLETION_READ_STATUS_EVENT)
        {
            Release(native);
            throw WinMLException.InvalidData($"Unexpected read status {(int)status}.");
        }

        outputEvent = new ConversationOutputEvent(Required(native));
        return true;
    }

    // Read the result before closing the stream.
    public ChatCompletionResult GetResult()
    {
        IWinMLChatCompletionResult* result = null;
        Check(Self->GetResult(&result));
        return new ChatCompletionResult(Required(result));
    }

    // The formatted prompt the stream generates from: its tokens, additional
    // stop strings, and any constraint it describes.
    public ConversationFormatResult GetFormatResult()
    {
        IWinMLConversationFormatResult* result = null;
        Check(Self->GetFormatResult(&result));
        return new ConversationFormatResult(Required(result));
    }

    // The parser that turns the generated text into the stream's events.
    public ConversationOutputParser GetOutputParser()
    {
        IWinMLConversationOutputParser* parser = null;
        Check(Self->GetOutputParser(&parser));
        return new ConversationOutputParser(Required(parser));
    }

    // Returns false when the request needs no constraint, such as a text
    // response without tools.
    public bool TryGetConstraint([NotNullWhen(true)] out TokenConstraint? constraint)
    {
        WINML_CONVERSATION_CONSTRAINT_PRESENCE presence;
        IWinMLTokenConstraint* native = null;
        Check(Self->GetConstraint(&presence, &native));
        if (presence != WINML_CONVERSATION_CONSTRAINT_PRESENCE.WINML_CONVERSATION_CONSTRAINT_PRESENCE_PRESENT || native == null)
        {
            Release(native);
            constraint = null;
            return false;
        }

        constraint = new TokenConstraint(native);
        return true;
    }

    // The text generation stream under this stream. The chat stream reads and
    // closes it: disposing the returned object releases a reference without
    // closing the stream, and reading from it takes tokens from the chat stream.
    public TextGenerationPullStream GetTextGenerationStream()
    {
        IWinMLTextGenerationPullStream* stream = null;
        Check(Self->GetTextGenerationStream(&stream));
        return new TextGenerationPullStream(Required(stream), borrowed: true);
    }

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

// One content, reasoning, or tool-call event. Its strings are copied on read.
public sealed unsafe class ConversationOutputEvent : WinMLObject, IWinMLInterface<ConversationOutputEvent>
{
    internal ConversationOutputEvent(IWinMLConversationOutputEvent* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLConversationOutputEvent.IID_Guid;

    static ConversationOutputEvent IWinMLInterface<ConversationOutputEvent>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLConversationOutputEvent*)pointer);

    private IWinMLConversationOutputEvent* Self => (IWinMLConversationOutputEvent*)ThisPtr;

    public ConversationOutputEventType Type
    {
        get
        {
            WINML_CONVERSATION_OUTPUT_EVENT_TYPE type;
            Check(Self->GetType(&type));
            return (ConversationOutputEventType)type;
        }
    }

    // The tool call that a tool event belongs to.
    public uint ToolCallIndex
    {
        get
        {
            uint index;
            Check(Self->GetToolCallIndex(&index));
            return index;
        }
    }

    public string CallId
    {
        get
        {
            PCWSTR text;
            Check(Self->GetCallId(&text));
            return Copy(text);
        }
    }

    public string ToolName
    {
        get
        {
            PCWSTR text;
            Check(Self->GetToolName(&text));
            return Copy(text);
        }
    }

    // The text a delta event adds.
    public string Text
    {
        get
        {
            PCWSTR text;
            Check(Self->GetText(&text));
            return Copy(text);
        }
    }

    // The failure an error event reports.
    public int GetError(out string message)
    {
        HRESULT error;
        PCWSTR text;
        Check(Self->GetError(&error, &text));
        message = Copy(text);
        return error.Value;
    }

    private string Copy(PCWSTR text)
    {
        string result = text.Value == null ? string.Empty : new string(text.Value);
        GC.KeepAlive(this);
        return result;
    }
}

// The outcome of one completion.
public sealed unsafe class ChatCompletionResult : WinMLObject, IWinMLInterface<ChatCompletionResult>
{
    internal ChatCompletionResult(IWinMLChatCompletionResult* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLChatCompletionResult.IID_Guid;

    static ChatCompletionResult IWinMLInterface<ChatCompletionResult>.CreateFromOwnedPointer(nint pointer) => new((IWinMLChatCompletionResult*)pointer);

    private IWinMLChatCompletionResult* Self => (IWinMLChatCompletionResult*)ThisPtr;

    // The reply, without reasoning or tool-call markup.
    public string Content
    {
        get
        {
            PCWSTR text;
            Check(Self->GetContent(&text));
            return Copy(text);
        }
    }

    public string ReasoningContent
    {
        get
        {
            PCWSTR text;
            Check(Self->GetReasoningContent(&text));
            return Copy(text);
        }
    }

    public ChatCompletionFinishReason FinishReason
    {
        get
        {
            WINML_CHAT_COMPLETION_FINISH_REASON reason;
            Check(Self->GetFinishReason(&reason));
            return (ChatCompletionFinishReason)reason;
        }
    }

    // The failure that ended the completion, or zero.
    public int ErrorCode
    {
        get
        {
            HRESULT error;
            Check(Self->GetErrorCode(&error));
            return error.Value;
        }
    }

    public ChatCompletionPromptCacheUse PromptCacheUse
    {
        get
        {
            WINML_CHAT_COMPLETION_PROMPT_CACHE cache;
            Check(Self->GetPromptCacheUse(&cache));
            return new ChatCompletionPromptCacheUse(
                (ChatCompletionPromptCacheMode)cache.mode,
                cache.promptTokenCount,
                cache.cachedPromptTokenCount,
                cache.prefilledPromptTokenCount);
        }
    }

    // The full formatted prompt and the generated tokens.
    public (uint PromptTokenCount, uint CompletionTokenCount) GetTokenCounts()
    {
        uint prompt;
        uint completion;
        Check(Self->GetTokenCounts(&prompt, &completion));
        return (prompt, completion);
    }

    public ConversationToolCall[] GetToolCalls()
    {
        uint count;
        Check(Self->GetToolCallCount(&count));
        ConversationToolCall[] calls = new ConversationToolCall[count];
        for (uint i = 0; i < count; ++i)
        {
            PCWSTR id;
            PCWSTR name;
            PCWSTR arguments;
            Check(Self->GetToolCall(i, &id, &name, &arguments));
            calls[i] = new ConversationToolCall(Copy(id), Copy(name), Copy(arguments));
        }

        return calls;
    }

    // The text generation result under the chat result: the raw generated
    // text and token IDs, with any reasoning and tool-call markup. Query it
    // for TextGenerationSpeculativeResult or TextGenerationWatermarkResult.
    public TextGenerationResult GetTextGenerationResult()
    {
        IWinMLTextGenerationResult* result = null;
        Check(Self->GetTextGenerationResult(&result));
        return new TextGenerationResult(Required(result));
    }

    private string Copy(PCWSTR text)
    {
        string result = text.Value == null ? string.Empty : new string(text.Value);
        GC.KeepAlive(this);
        return result;
    }
}

// The generated tokens that one event of a chat completion stream came from.
// Query a ConversationOutputEvent from ChatCompletionPullStream for it.
public sealed unsafe class ChatCompletionEventProvenance : WinMLObject, IWinMLInterface<ChatCompletionEventProvenance>
{
    private ChatCompletionEventProvenance(IWinMLChatCompletionEventProvenance* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLChatCompletionEventProvenance.IID_Guid;

    static ChatCompletionEventProvenance IWinMLInterface<ChatCompletionEventProvenance>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLChatCompletionEventProvenance*)pointer);

    // The event's tokens: TokenCount completion tokens from FirstTokenIndex on.
    public (uint FirstTokenIndex, uint TokenCount) SourceTokenRange
    {
        get
        {
            uint first;
            uint count;
            Check(((IWinMLChatCompletionEventProvenance*)ThisPtr)->GetSourceTokenRange(&first, &count));
            return (first, count);
        }
    }

    public uint[] GetSourceTokenIds()
    {
        uint count = 0;
        uint* tokens = null;
        Check(((IWinMLChatCompletionEventProvenance*)ThisPtr)->GetSourceTokenIds(&count, &tokens));
        return Tokenizer.TakeTokenIds(tokens, count);
    }
}

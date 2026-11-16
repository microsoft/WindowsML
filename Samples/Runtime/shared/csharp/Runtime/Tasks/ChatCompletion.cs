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
}

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

    private string Copy(PCWSTR text)
    {
        string result = text.Value == null ? string.Empty : new string(text.Value);
        GC.KeepAlive(this);
        return result;
    }
}

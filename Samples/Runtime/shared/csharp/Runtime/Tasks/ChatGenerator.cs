// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. Streams chat completion events as
// IAsyncEnumerable. The generator owns a text generation session and the chat
// session that wraps it on a SessionThread, so it can be used from any thread
// or async context. The chat session keeps the tokens of the previous turn,
// so a request that repeats the conversation so far evaluates only what is
// new. Cancelling the token, or leaving the loop early, cancels generation in
// the Runtime.

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace WinMLSamples.Runtime.Tasks;

// One event of a completion, copied from the Runtime. Text is the delta of a
// content, reasoning, or tool-arguments event; CallId and ToolName are set on
// a ToolCallStart event; Text is the message and ErrorCode the HRESULT of an
// Error event.
public readonly record struct ChatCompletionUpdate(
    ConversationOutputEventType Type,
    string Text,
    uint ToolCallIndex = 0,
    string? CallId = null,
    string? ToolName = null,
    int ErrorCode = 0);

// The outcome of a completion, copied from the Runtime's result.
public sealed record ChatCompletionOutcome(
    string Content,
    string ReasoningContent,
    IReadOnlyList<ConversationToolCall> ToolCalls,
    uint PromptTokenCount,
    uint CompletionTokenCount,
    ChatCompletionPromptCacheUse PromptCacheUse,
    ChatCompletionFinishReason FinishReason,
    int ErrorCode);

public sealed class ChatGenerator : IAsyncDisposable, IDisposable
{
    private readonly SessionThread _thread;
    private readonly WinMLTasks _tasks;
    private readonly TextGenerationSession _textSession;
    private readonly ChatCompletionTask _chatTask;
    private readonly ChatCompletionSession _session;
    private readonly object _gate = new();
    private CancellationSource? _activeCancellation;
    private ChatCompletionOutcome? _lastOutcome;
    private int _generating;
    private int _disposed;

    private ChatGenerator(
        SessionThread thread,
        WinMLTasks tasks,
        TextGenerationSession textSession,
        ChatCompletionTask chatTask,
        ChatCompletionSession session,
        TextGenerationCapabilities capabilities)
    {
        _thread = thread;
        _tasks = tasks;
        _textSession = textSession;
        _chatTask = chatTask;
        _session = session;
        Capabilities = capabilities;
    }

    // What the text generation session supports, read when the generator was
    // created; its SequenceState is not updated afterward. Tool calls and JSON
    // schema output are reliable only when it supports constraints.
    public TextGenerationCapabilities Capabilities { get; }

    // The outcome of the most recent completion that ran to completion or was
    // canceled; null while generating and after a completion that failed to run.
    public ChatCompletionOutcome? LastOutcome => Volatile.Read(ref _lastOutcome);

    // Creates a text generation session for configuration, and a chat session
    // over it, on a new SessionThread. The session consumes the configuration.
    // tokenizer must be the configuration's tokenizer, which task also uses.
    // sessionCreated, if given, runs on the session thread once the text
    // generation session exists and before the chat task is created; an
    // exception it throws fails creation.
    public static async Task<ChatGenerator> CreateAsync(
        WinMLTasks tasks,
        TextGenerationTask task,
        TextGenerationConfiguration configuration,
        Tokenizer tokenizer,
        Action<TextGenerationSession>? sessionCreated = null)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(tokenizer);
        SessionThread thread = new("Windows ML chat session");
        try
        {
            return await thread.InvokeAsync(() =>
            {
                TextGenerationSession textSession = task.CreateSession(configuration);
                ChatCompletionTask? chatTask = null;
                try
                {
                    sessionCreated?.Invoke(textSession);
                    chatTask = tasks.ChatCompletion.CreateChatCompletionTask(tokenizer);
                    ChatCompletionSession session = chatTask.CreateSession(textSession);
                    return new ChatGenerator(thread, tasks, textSession, chatTask, session, textSession.Capabilities);
                }
                catch
                {
                    chatTask?.Dispose();
                    textSession.Dispose();
                    throw;
                }
            }).ConfigureAwait(false);
        }
        catch
        {
            thread.Dispose();
            throw;
        }
    }

    // Completes request: the whole conversation so far. options limit and
    // shape generation; the generator does not keep them.
    public IAsyncEnumerable<ChatCompletionUpdate> GenerateAsync(
        ConversationRequest request,
        TextGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        return StreamAsync(request, options, cancellationToken);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // Closes the chat and text generation sessions on their thread and
    // reports a failure to close. Dispose the generator as usual afterward.
    public Task CloseAsync()
    {
        ThrowIfDisposed();
        return _thread.InvokeAsync(() =>
        {
            _session.Close();
            _textSession.Close();
        });
    }

    // Cancels any completion in progress, closes both sessions on their thread
    // without reporting a failure to close, then stops the thread.
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            _activeCancellation?.Cancel();
        }

        await _thread.InvokeAsync(() =>
        {
            _session.Dispose();
            _chatTask.Dispose();
            _textSession.Dispose();
        }).ConfigureAwait(false);
        _thread.Dispose();
    }

    // Yields the stream's events as the session thread reads them. Throws
    // OperationCanceledException when the completion was canceled and the
    // mapped exception when it failed. LastOutcome reports the outcome of a
    // completion that ran, including a canceled one.
    private async IAsyncEnumerable<ChatCompletionUpdate> StreamAsync(
        ConversationRequest request,
        TextGenerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _generating, 1) != 0)
        {
            throw new InvalidOperationException("The generator is already generating. Complete or dispose the previous enumeration first.");
        }

        Channel<ChatCompletionUpdate> channel = Channel.CreateUnbounded<ChatCompletionUpdate>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        CancellationSource? cancellation = null;
        CancellationTokenRegistration registration = default;
        Task? producer = null;
        bool completed = false;
        ChatCompletionOutcome? outcome = null;
        try
        {
            Volatile.Write(ref _lastOutcome, null);
            CancellationSource source = _tasks.CreateCancellationSource();
            cancellation = source;
            // DisposeAsync marks the object disposed before it takes the gate, so
            // recheck under the gate: either this operation fails here, or
            // DisposeAsync sees its source and cancels it.
            lock (_gate)
            {
                ThrowIfDisposed();
                _activeCancellation = source;
            }

            registration = cancellationToken.Register(static state => ((CancellationSource)state!).Cancel(), source);
            producer = _thread.InvokeAsync(() =>
            {
                try
                {
                    using ChatCompletionPullStream stream = _session.Generate(request, options, source);
                    while (stream.TryReadNext(out ConversationOutputEvent? outputEvent))
                    {
                        using (outputEvent)
                        {
                            channel.Writer.TryWrite(ReadUpdate(outputEvent));
                        }
                    }

                    // The result is readable only until the stream closes.
                    using ChatCompletionResult result = stream.GetResult();
                    (uint promptTokens, uint completionTokens) = result.GetTokenCounts();
                    outcome = new ChatCompletionOutcome(
                        result.Content,
                        result.ReasoningContent,
                        result.GetToolCalls(),
                        promptTokens,
                        completionTokens,
                        result.PromptCacheUse,
                        result.FinishReason,
                        result.ErrorCode);
                    Volatile.Write(ref _lastOutcome, outcome);
                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                }
            });

            await foreach (ChatCompletionUpdate update in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                yield return update;
            }

            completed = true;
        }
        finally
        {
            if (!completed)
            {
                cancellation?.Cancel();
            }

            await registration.DisposeAsync().ConfigureAwait(false);
            if (producer is not null)
            {
                await producer.ConfigureAwait(false);
            }

            lock (_gate)
            {
                _activeCancellation = null;
            }

            cancellation?.Dispose();
            Volatile.Write(ref _generating, 0);
        }

        if (outcome?.FinishReason == ChatCompletionFinishReason.Canceled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (outcome?.FinishReason == ChatCompletionFinishReason.Error)
        {
            throw WinMLException.GetExceptionForHResult(outcome.ErrorCode);
        }
    }

    // Reads only the fields that the event's type defines.
    private static ChatCompletionUpdate ReadUpdate(ConversationOutputEvent outputEvent)
    {
        ConversationOutputEventType type = outputEvent.Type;
        switch (type)
        {
            case ConversationOutputEventType.ContentDelta:
            case ConversationOutputEventType.ReasoningDelta:
                return new ChatCompletionUpdate(type, outputEvent.Text);
            case ConversationOutputEventType.ToolCallStart:
                return new ChatCompletionUpdate(type, string.Empty, outputEvent.ToolCallIndex, outputEvent.CallId, outputEvent.ToolName);
            case ConversationOutputEventType.ToolArgumentsDelta:
                return new ChatCompletionUpdate(type, outputEvent.Text, outputEvent.ToolCallIndex);
            case ConversationOutputEventType.ToolCallEnd:
                return new ChatCompletionUpdate(type, string.Empty, outputEvent.ToolCallIndex);
            case ConversationOutputEventType.Error:
                int errorCode = outputEvent.GetError(out string message);
                return new ChatCompletionUpdate(type, message, ErrorCode: errorCode);
            default:
                return new ChatCompletionUpdate(type, string.Empty);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

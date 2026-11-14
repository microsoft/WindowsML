// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. Streams text generation as
// IAsyncEnumerable. The generator owns a session on a SessionThread, so it
// can be used from any thread or async context. Cancelling the token, or
// leaving the loop early, cancels generation in the Runtime.

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace WinMLSamples.Runtime.Tasks;

// One generated token and its text.
public readonly record struct TextGenerationUpdate(uint TokenId, string Text);

public sealed class TextGenerator : IAsyncDisposable, IDisposable
{
    private readonly SessionThread _thread;
    private readonly WinMLTasks _tasks;
    private readonly TextGenerationSession _session;
    private readonly object _gate = new();
    private CancellationSource? _activeCancellation;
    private TextGenerationResult? _lastResult;
    private int _generating;
    private int _disposed;

    private TextGenerator(SessionThread thread, WinMLTasks tasks, TextGenerationSession session)
    {
        _thread = thread;
        _tasks = tasks;
        _session = session;
    }

    // The result of the most recent generation that ran to completion or was
    // canceled; null while generating and after a generation that failed to
    // run. The generator owns it until the next generation.
    public TextGenerationResult? LastResult => Volatile.Read(ref _lastResult);

    // Creates a session for configuration on a new SessionThread. The session
    // consumes the configuration. Session creation errors, such as a missing
    // engine payload, surface here.
    public static async Task<TextGenerator> CreateAsync(WinMLTasks tasks, TextGenerationTask task, TextGenerationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(configuration);
        SessionThread thread = new();
        try
        {
            TextGenerationSession session = await thread.InvokeAsync(() => task.CreateSession(configuration)).ConfigureAwait(false);
            return new TextGenerator(thread, tasks, session);
        }
        catch
        {
            thread.Dispose();
            throw;
        }
    }

    // Runs function with the session on the session thread.
    public Task<T> InvokeAsync<T>(Func<TextGenerationSession, T> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        ThrowIfDisposed();
        return _thread.InvokeAsync(() => function(_session));
    }

    // Generates from a prompt, which the session encodes without a chat template.
    public IAsyncEnumerable<TextGenerationUpdate> GenerateTextAsync(string prompt, TextGenerationOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return StreamAsync((session, o, c) => session.GenerateText(prompt, o, c), options, cancellationToken);
    }

    // Replaces the session's sequence with tokenIds and generates from it. The
    // memory must not change until the enumeration completes.
    public IAsyncEnumerable<TextGenerationUpdate> GenerateTokensAsync(ReadOnlyMemory<uint> tokenIds, TextGenerationOptions options, CancellationToken cancellationToken = default)
    {
        return StreamAsync((session, o, c) => session.GenerateTokens(tokenIds.Span, o, c), options, cancellationToken);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // Closes the session on its thread and reports a failure to close. The
    // generator cannot generate afterward; dispose it as usual.
    public Task CloseAsync()
    {
        ThrowIfDisposed();
        return _thread.InvokeAsync(_session.Close);
    }

    // Cancels any generation in progress, closes the session on its thread
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

        await _thread.InvokeAsync(_session.Dispose).ConfigureAwait(false);
        Interlocked.Exchange(ref _lastResult, null)?.Dispose();
        _thread.Dispose();
    }

    // Yields the stream's tokens as the session thread reads them. Throws
    // OperationCanceledException when generation was canceled and the mapped
    // exception when it failed. LastResult reports the outcome of a generation
    // that ran, including a canceled one.
    private async IAsyncEnumerable<TextGenerationUpdate> StreamAsync(
        Func<TextGenerationSession, TextGenerationOptions, CancellationSource, TextGenerationPullStream> start,
        TextGenerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _generating, 1) != 0)
        {
            throw new InvalidOperationException("The generator is already generating. Complete or dispose the previous enumeration first.");
        }

        Channel<TextGenerationUpdate> channel = Channel.CreateUnbounded<TextGenerationUpdate>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        CancellationSource? cancellation = null;
        CancellationTokenRegistration registration = default;
        Task? producer = null;
        bool completed = false;
        TextGenerationFinishReason? finishReason = null;
        int errorCode = 0;
        try
        {
            Interlocked.Exchange(ref _lastResult, null)?.Dispose();
            CancellationSource source = _tasks.CreateCancellationSource();
            cancellation = source;
            lock (_gate)
            {
                _activeCancellation = source;
            }

            registration = cancellationToken.Register(static state => ((CancellationSource)state!).Cancel(), source);
            producer = _thread.InvokeAsync(() =>
            {
                try
                {
                    using TextGenerationPullStream stream = start(_session, options, source);
                    while (stream.TryReadNext(out uint tokenId, out ReadOnlySpan<char> text))
                    {
                        channel.Writer.TryWrite(new TextGenerationUpdate(tokenId, new string(text)));
                    }

                    // Read the outcome here, before DisposeAsync can release the result.
                    TextGenerationResult result = stream.GetResult();
                    finishReason = result.FinishReason;
                    errorCode = result.ErrorCode;
                    Volatile.Write(ref _lastResult, result);
                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                }
            });

            await foreach (TextGenerationUpdate update in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
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

        if (finishReason == TextGenerationFinishReason.Canceled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (finishReason == TextGenerationFinishReason.Error)
        {
            throw WinMLException.GetExceptionForHResult(errorCode);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

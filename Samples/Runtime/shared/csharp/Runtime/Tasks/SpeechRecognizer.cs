// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. Streams speech recognition as
// IAsyncEnumerable. The recognizer owns a session on a SessionThread, so it
// can be used from any thread or async context. Cancelling the token, or
// leaving the loop early, cancels recognition in the Runtime.

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace WinMLSamples.Runtime.Tasks;

// One update, prediction, or need-input notice of an operation.
public readonly record struct SpeechRecognitionUpdate(SpeechRecognitionReadStatus Status, string Text);

// The outcome of an operation, copied from the Runtime's result.
public sealed record SpeechRecognitionOutcome(string Transcript, SpeechRecognitionFinishReason FinishReason, int ErrorCode);

public sealed class SpeechRecognizer : IAsyncDisposable, IDisposable
{
    private readonly SessionThread _thread;
    private readonly WinMLTasks _tasks;
    private readonly SpeechRecognitionSession _session;
    private readonly object _gate = new();
    private CancellationSource? _activeCancellation;
    private SpeechRecognitionOutcome? _lastOutcome;
    private int _recognizing;
    private int _disposed;

    private SpeechRecognizer(SessionThread thread, WinMLTasks tasks, SpeechRecognitionSession session)
    {
        _thread = thread;
        _tasks = tasks;
        _session = session;
    }

    // The outcome of the most recent operation that ran to completion or was
    // canceled; null while recognizing and after an operation that failed to run.
    public SpeechRecognitionOutcome? LastOutcome => Volatile.Read(ref _lastOutcome);

    // Creates a session for configuration on a new SessionThread. The session
    // consumes the configuration. Session creation errors, such as a language
    // or mode the model cannot honor, surface here.
    public static async Task<SpeechRecognizer> CreateAsync(WinMLTasks tasks, AutomaticSpeechRecognitionTask task, SpeechRecognitionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(configuration);
        SessionThread thread = new("Windows ML speech recognition session");
        try
        {
            SpeechRecognitionSession session = await thread.InvokeAsync(() => task.CreateSession(configuration)).ConfigureAwait(false);
            return new SpeechRecognizer(thread, tasks, session);
        }
        catch
        {
            thread.Dispose();
            throw;
        }
    }

    // Runs function with the session on the session thread, such as to query
    // it for SpeechRecognitionLiveSession and drive live input.
    public Task<T> InvokeAsync<T>(Func<SpeechRecognitionSession, T> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        ThrowIfDisposed();
        return _thread.InvokeAsync(() => function(_session));
    }

    // Transcribes the valid span of a waveform tensor.
    public IAsyncEnumerable<SpeechRecognitionUpdate> TranscribeWaveformAsync(
        Tensor waveform,
        SpeechRecognitionWaveformMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(waveform);
        return StreamAsync((session, c) => session.TranscribeWaveform(waveform, metadata, c), cancellationToken);
    }

    // Transcribes encoder input that the application computed. Throws
    // InvalidCastException when the session does not take encoder input.
    public IAsyncEnumerable<SpeechRecognitionUpdate> TranscribeEncoderInputAsync(Tensor encoderInput, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encoderInput);
        return StreamAsync(
            (session, c) =>
            {
                using SpeechRecognitionEncoderInput input = session.As<SpeechRecognitionEncoderInput>();
                return input.TranscribeEncoderInput(encoderInput, c);
            },
            cancellationToken);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // Closes the session on its thread and reports a failure to close. The
    // recognizer cannot recognize afterward; dispose it as usual.
    public Task CloseAsync()
    {
        ThrowIfDisposed();
        return _thread.InvokeAsync(_session.Close);
    }

    // Cancels any operation in progress, closes the session on its thread
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
        _thread.Dispose();
    }

    // Yields the stream's updates as the session thread reads them. Throws
    // OperationCanceledException when the operation was canceled and the
    // mapped exception when it failed. LastOutcome reports the outcome of an
    // operation that ran, including a canceled one.
    private async IAsyncEnumerable<SpeechRecognitionUpdate> StreamAsync(
        Func<SpeechRecognitionSession, CancellationSource, SpeechRecognitionPullStream> start,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _recognizing, 1) != 0)
        {
            throw new InvalidOperationException("The recognizer is already recognizing. Complete or dispose the previous enumeration first.");
        }

        Channel<SpeechRecognitionUpdate> channel = Channel.CreateUnbounded<SpeechRecognitionUpdate>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        CancellationSource? cancellation = null;
        CancellationTokenRegistration registration = default;
        Task? producer = null;
        bool completed = false;
        SpeechRecognitionOutcome? outcome = null;
        try
        {
            Volatile.Write(ref _lastOutcome, null);
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
                    using SpeechRecognitionPullStream stream = start(_session, source);
                    while (stream.TryReadNext(out SpeechRecognitionReadStatus status, out ReadOnlySpan<char> text))
                    {
                        channel.Writer.TryWrite(new SpeechRecognitionUpdate(status, new string(text)));
                    }

                    // Read the outcome here, on the session's thread.
                    using SpeechRecognitionResult result = stream.GetResult();
                    outcome = new SpeechRecognitionOutcome(result.Transcript, result.FinishReason, result.ErrorCode);
                    Volatile.Write(ref _lastOutcome, outcome);
                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                }
            });

            await foreach (SpeechRecognitionUpdate update in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
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
            Volatile.Write(ref _recognizing, 0);
        }

        if (outcome?.FinishReason == SpeechRecognitionFinishReason.Canceled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (outcome?.FinishReason == SpeechRecognitionFinishReason.Error)
        {
            throw WinMLException.GetExceptionForHResult(outcome.ErrorCode);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

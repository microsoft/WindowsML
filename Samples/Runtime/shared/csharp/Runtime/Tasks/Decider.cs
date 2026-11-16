// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. Runs decisions from any thread or async
// context. The decider owns a decision session on a SessionThread; cancelling
// the token cancels the decision in the Runtime before its next question.

namespace WinMLSamples.Runtime.Tasks;

public sealed class Decider : IAsyncDisposable, IDisposable
{
    private readonly SessionThread _thread;
    private readonly WinMLTasks _tasks;
    private readonly DecisionSession _session;
    private readonly object _gate = new();
    private CancellationSource? _activeCancellation;
    private int _disposed;

    private Decider(SessionThread thread, WinMLTasks tasks, DecisionSession session)
    {
        _thread = thread;
        _tasks = tasks;
        _session = session;
    }

    // Creates a session on a new SessionThread. The session prepares the
    // model for the task's target, so preparation errors surface here.
    public static async Task<Decider> CreateAsync(WinMLTasks tasks, DecisionTask task)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(task);
        SessionThread thread = new("Windows ML decision session");
        try
        {
            DecisionSession session = await thread.InvokeAsync(task.CreateSession).ConfigureAwait(false);
            return new Decider(thread, tasks, session);
        }
        catch
        {
            thread.Dispose();
            throw;
        }
    }

    // Evaluates request on the session thread. The result is free-threaded;
    // dispose it. Throws OperationCanceledException when cancellationToken
    // cancels the decision.
    public async Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationSource cancellation = _tasks.CreateCancellationSource();
        lock (_gate)
        {
            if (_activeCancellation is not null)
            {
                throw new InvalidOperationException("The decider is already deciding. Await the previous decision first.");
            }

            _activeCancellation = cancellation;
        }

        try
        {
            using CancellationTokenRegistration registration =
                cancellationToken.Register(static state => ((CancellationSource)state!).Cancel(), cancellation);
            try
            {
                return await _thread.InvokeAsync(() => _session.Decide(request, cancellation)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        finally
        {
            lock (_gate)
            {
                _activeCancellation = null;
            }
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // Closes the session on its thread and reports a failure to close.
    public Task CloseAsync()
    {
        ThrowIfDisposed();
        return _thread.InvokeAsync(_session.Close);
    }

    // Cancels any decision in progress, closes the session on its thread
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

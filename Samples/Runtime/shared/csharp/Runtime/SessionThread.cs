// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. A dedicated thread for Windows ML objects
// whose methods are bound to their creation thread, such as text generation
// sessions and their streams. Create such objects and call their methods
// through InvokeAsync; their final release is free-threaded.

using System.Collections.Concurrent;

namespace WinMLSamples.Runtime;

public sealed class SessionThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private int _disposed;

    public SessionThread(string name = "Windows ML session")
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync(() =>
        {
            action();
            return true;
        });
    }

    // Runs function on the thread. The task faults with its exception.
    public Task<T> InvokeAsync<T>(Func<T> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsCurrentThread)
        {
            try
            {
                return Task.FromResult(function());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try
            {
                completion.TrySetResult(function());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    // Completes queued work and stops the thread.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        if (!IsCurrentThread)
        {
            _thread.Join();
        }
    }

    private void Run()
    {
        foreach (Action action in _queue.GetConsumingEnumerable())
        {
            action();
        }

        _queue.Dispose();
    }
}

using System.Collections.Concurrent;

namespace TShock.AgentBridge;

/// <summary>
/// Marshals work onto the Terraria game thread. Every world mutation must run through
/// this queue; HTTP handlers run on thread-pool threads and must never touch the world.
/// </summary>
internal sealed class MainThreadDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private long _ticks;

    /// <summary>Number of game ticks observed; zero while the server is idle.</summary>
    public long Ticks => Interlocked.Read(ref _ticks);

    public Task<T> InvokeAsync<T>(Func<T> action, TimeSpan? timeout = null)
    {
        if (_queue.IsAddingCompleted)
        {
            return Task.FromException<T>(new ObjectDisposedException(nameof(MainThreadDispatcher)));
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        // A slow or blocked game thread must surface as a timeout, never as a hung request.
        return completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));
    }

    /// <summary>Drains the queue; called once per game tick from the update hook.</summary>
    public void Pump()
    {
        Interlocked.Increment(ref _ticks);

        while (!_queue.IsCompleted && _queue.TryTake(out var work))
        {
            work();
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _queue.Dispose();
    }
}

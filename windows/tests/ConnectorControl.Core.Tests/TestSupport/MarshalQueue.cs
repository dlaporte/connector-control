using System.Collections.Concurrent;

namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>
/// A stand-in for the WPF dispatcher: callbacks from watcher/timer threads are
/// queued and run only when the test pumps, so the test thread stays the single
/// owner of AppState exactly like the UI thread does in the app.
/// </summary>
public sealed class MarshalQueue
{
    private readonly ConcurrentQueue<Action> queue = new();
    private TaskCompletionSource posted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Pending => queue.Count;

    public void Post(Action action)
    {
        queue.Enqueue(action);
        Interlocked.Exchange(ref posted, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>
    /// Completes once something is waiting to be pumped: at once if something already is, else at
    /// the next <see cref="Post"/>. C#-only: for a post that arrives from a pool continuation (the
    /// update coordinator's), which no seam runs for the test. Awaiting it holds no thread, where
    /// <see cref="PumpUntil"/> sleeps on one, so the waiting test does not add to the starvation
    /// that delays that continuation.
    /// </summary>
    public Task WhenPostedAsync()
    {
        // The signal is read before the queue: a Post racing this call either enqueued before the
        // count (seen as pending) or completes the very signal read here.
        var signal = Volatile.Read(ref posted);
        return queue.IsEmpty ? signal.Task : Task.CompletedTask;
    }

    /// <summary>Runs everything queued so far; returns how many actions ran.</summary>
    public int Pump()
    {
        var ran = 0;
        while (queue.TryDequeue(out var action))
        {
            action();
            ran++;
        }
        return ran;
    }

    /// <summary>
    /// Runs the oldest queued action, if there is one; returns how many ran (0 or 1). For a test that
    /// must run one post and leave the next for <see cref="WhenPostedAsync"/>: a post a pool thread
    /// queues while that one runs would be run by <see cref="Pump"/> too.
    /// </summary>
    public int PumpOne()
    {
        if (!queue.TryDequeue(out var action))
        {
            return 0;
        }
        action();
        return 1;
    }

    /// <summary>Pumps until the condition holds or the timeout passes.</summary>
    public bool PumpUntil(Func<bool> condition, TimeSpan timeout) =>
        Wait.Until(() => { Pump(); return condition(); }, timeout);
}

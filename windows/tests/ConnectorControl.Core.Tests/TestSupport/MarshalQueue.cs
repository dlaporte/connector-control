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

    public int Pending => queue.Count;

    public void Post(Action action) => queue.Enqueue(action);

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

    /// <summary>Pumps until the condition holds or the timeout passes.</summary>
    public bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Pump();
            if (condition())
            {
                return true;
            }
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            Thread.Sleep(50);
        }
    }
}

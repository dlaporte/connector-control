namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>
/// A TimeProvider whose timers fire only when the test says so: FileWatcher's debounce, so a test
/// can see that a re-check is pending and run it at a moment of its choosing instead of sleeping
/// past it. Only CreateTimer is replaced; the clock is the system's. C#-only: the Mac's watcher
/// has no debounce.
/// </summary>
public sealed class ManualTimers : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (gate)
        {
            timers.Add(timer);
        }
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Whether a timer is armed: for FileWatcher, a re-check waiting to run. A
    /// FileSystemWatcher event arms it from the watcher's own thread, so a test waiting for one
    /// polls this.</summary>
    public bool Pending
    {
        get { lock (gate) { return timers.Any(t => t.Armed); } }
    }

    /// <summary>Fires every armed timer once, on the calling thread, as if its due time had come;
    /// returns how many fired. Each is disarmed first, as a one-shot timer is when it fires.</summary>
    public int FireAll()
    {
        List<ManualTimer> due;
        lock (gate)
        {
            due = timers.Where(t => t.Armed).ToList();
            foreach (var timer in due)
            {
                timer.Armed = false;
            }
        }
        foreach (var timer in due)
        {
            timer.Fire();
        }
        return due.Count;
    }

    private sealed class ManualTimer(ManualTimers owner, TimerCallback callback, object? state) : ITimer
    {
        public bool Armed { get; set; }
        private bool disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                if (disposed)
                {
                    return false;
                }
                Armed = dueTime != Timeout.InfiniteTimeSpan;
                return true;
            }
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner.gate)
            {
                disposed = true;
                Armed = false;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

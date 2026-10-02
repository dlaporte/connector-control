namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>
/// The one condition-polling loop every test that waits on a background thread (a
/// watcher callback, a queued marshal, …) should share, instead of hand-rolling its
/// own deadline-plus-sleep loop. Checks once more after the deadline passes, so a
/// condition that becomes true exactly as the timeout expires is not missed.
/// </summary>
internal static class Wait
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The one ceiling for a wait on real background work that is expected to finish: a
    /// FileSystemWatcher event, a timer, a pool continuation. Generous on purpose: a wait that
    /// succeeds returns the moment its condition holds, so only a failing test ever pays it,
    /// while a hosted runner whose thread pool the parallel suite has starved can take many
    /// seconds to run the work at all (5 and 8 s were not always enough). Work a test can run
    /// itself goes through a seam instead (AppHost's Background and Delay); the windows that
    /// prove something does NOT happen are short and named where they are used.
    /// </summary>
    public static readonly TimeSpan Eventually = TimeSpan.FromSeconds(30);

    public static bool Until(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(PollInterval);
        }
        return condition();
    }
}

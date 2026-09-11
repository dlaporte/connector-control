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

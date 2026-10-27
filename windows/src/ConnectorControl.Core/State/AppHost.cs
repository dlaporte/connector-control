namespace ConnectorControl.Core.State;

/// <summary>
/// The UI thread as four delegates: <c>Marshal</c> posts an action to it (the
/// WPF Dispatcher in the app, a queue in tests), <c>Delay</c> schedules one
/// there later (DispatcherTimer / captured list), <c>Now</c> is the clock, and
/// <c>Background</c> runs work off it (the thread pool in the app, a queue the
/// test drains in tests, so a probe never waits on a pool thread that the parallel
/// suite has parked in a sleep).
/// <para>
/// <c>Marshal</c> POSTS and must never block: <c>FileWatcher</c> calls it from a
/// timer thread and the toast notifier from a COM MTA thread, so a blocking
/// implementation would deadlock the moment either side waits on the other. It
/// has therefore NOT necessarily run when it returns — a caller that needs the
/// result back awaits <c>MarshalAsync</c> instead of reading a captured local.
/// </para>
/// <para>
/// <c>ControlToolPath</c> is the full path Claude's entry for Connector Control names, as the app
/// that hosts this state supplies it, or null where there is none — a development run, and the
/// tests unless one sets it. The model never works a path out for itself: on the Mac the app
/// passes the tool inside its own bundle, and on Windows the copy it keeps outside the update
/// folder. Whatever is passed is what the next apply writes, so a moved or updated app corrects
/// it then.
/// </para>
/// </summary>
public sealed record AppHost(Action<Action> Marshal, Action<TimeSpan, Action> Delay, Func<DateTime> Now, Action<Action> Background, string? ControlToolPath = null)
{
    /// <summary>Everything runs immediately on the calling thread.</summary>
    internal static AppHost Inline() => new(action => action(), (_, action) => action(), () => DateTime.UtcNow, action => action());

    /// <summary>Runs <paramref name="work"/> on the UI thread and completes with what it returned (or threw).</summary>
    public Task<T> MarshalAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Marshal(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }
}

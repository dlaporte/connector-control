namespace ConnectorControl.Core.State;

/// <summary>
/// The UI thread as three delegates: <c>Marshal</c> posts an action to it (the
/// WPF Dispatcher in the app, a queue in tests), <c>Delay</c> schedules one
/// there later (DispatcherTimer / captured list), <c>UtcNow</c> is the clock.
/// <para>
/// <c>Marshal</c> POSTS and must never block: <c>FileWatcher</c> calls it from a
/// timer thread and the toast notifier from a COM MTA thread, so a blocking
/// implementation would deadlock the moment either side waits on the other. It
/// has therefore NOT necessarily run when it returns — a caller that needs the
/// result back awaits <c>MarshalAsync</c> instead of reading a captured local.
/// </para>
/// </summary>
public sealed record AppHost(Action<Action> Marshal, Action<TimeSpan, Action> Delay, Func<DateTime> UtcNow)
{
    /// <summary>Everything runs immediately on the calling thread.</summary>
    public static AppHost Inline() => new(action => action(), (_, action) => action(), () => DateTime.UtcNow);

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

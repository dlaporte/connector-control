namespace ConnectorControl.App;

/// <summary>
/// One tray icon per logon session. The first instance owns the mutex and waits
/// on a named event; a second launch sets that event so the running app shows
/// its flyout and then exits — the Windows answer to re-opening a bundled app on
/// the Mac, and the difference between "nothing happened" and "there it is".
/// The one launch that must stay silent is Windows starting us with
/// <c>-ToastActivated</c> to deliver a toast click: the toolkit's COM server routes
/// that to the running process on its own, so the second instance just leaves.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\ConnectorControl.SingleInstance";
    public const string ShowEventName = @"Local\ConnectorControl.ShowFlyout";
    public const string ToastActivatedArgument = "-ToastActivated";

    private readonly Mutex mutex;
    private readonly EventWaitHandle showRequested;
    private RegisteredWaitHandle? registration;
    private bool owned;

    public SingleInstance()
    {
        mutex = new Mutex(initiallyOwned: true, MutexName, out owned);
        showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
    }

    /// <summary>False when another instance already owns the session.</summary>
    public bool IsFirstInstance => owned;

    /// <summary>Windows started this process to deliver a toast activation, not because a user asked for the app.</summary>
    public static bool IsToastActivation(IEnumerable<string> arguments) =>
        arguments.Any(a => string.Equals(a, ToastActivatedArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>Second instance: ask the running one to come forward.</summary>
    public void SignalShow() => showRequested.Set();

    /// <summary>First instance: run <paramref name="show"/> each time another launch signals. The callback arrives on a pool thread — marshal.</summary>
    public void OnShowRequested(Action show) =>
        registration = ThreadPool.RegisterWaitForSingleObject(showRequested, (_, _) => show(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        registration?.Unregister(null);
        registration = null;
        if (owned)
        {
            mutex.ReleaseMutex();   // an abandoned mutex makes the NEXT launch look like a crash recovery
            owned = false;
        }
        showRequested.Dispose();
        mutex.Dispose();
    }
}

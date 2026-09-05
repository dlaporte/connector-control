using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.State;

/// <summary>
/// Spec §6.7: check 10 s after launch and every 24 h; with autoUpdate on,
/// download silently, stage for quit, and toast once per version; otherwise —
/// and always for Check for Updates… — show the update dialog. Manual checks
/// report "up to date" and failures; background checks stay silent.
/// </summary>
public sealed class UpdateCoordinator : IDisposable
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public const string ReadyToastBody = "An update to Connector Control is ready and will install when you quit.";
    public const string UpToDateMessage = "You're up to date.";
    public const string CheckFailedMessage = "Couldn’t check for updates.";
    public const string AvailableHeadline = "A new version of Connector Control is available!";
    public const string InstallButton = "Install and Relaunch";
    public const string LaterButton = "Later";

    public static string UpToDateDetail(string version) => $"Connector Control {version} is currently the newest version available.";

    public static string AvailableDetail(string newVersion, string currentVersion) =>
        $"Connector Control {newVersion} is now available — you have {currentVersion}. Would you like to install it now?";

    private readonly IUpdater updater;
    private readonly ISettings settings;
    private readonly INotifier notifier;
    private readonly IDialogs dialogs;
    private readonly AppHost host;
    private bool started;
    private bool disposed;

    public UpdateCoordinator(IUpdater updater, ISettings settings, INotifier notifier, IDialogs dialogs, AppHost host)
    {
        this.updater = updater;
        this.settings = settings;
        this.notifier = notifier;
        this.dialogs = dialogs;
        this.host = host;
    }

    /// <summary>The version the ready toast was shown for (once per version).</summary>
    public string? NotifiedVersion { get; private set; }

    /// <summary>The version already handed to ApplyOnQuit, so a later check does not stage it twice.</summary>
    public string? StagedVersion { get; private set; }

    public void Start()
    {
        if (!updater.IsAvailable || started)
        {
            return;
        }
        started = true;
        host.Delay(InitialDelay, Tick);
    }

    private void Tick()
    {
        if (disposed)
        {
            return;
        }
        _ = CheckAsync(interactive: false);
        host.Delay(Interval, Tick);
    }

    /// <summary><paramref name="interactive"/>: Settings ▸ Check for Updates… (always shows a result); false for scheduled checks.</summary>
    public async Task<UpdateOutcome> CheckAsync(bool interactive)
    {
        if (!updater.IsAvailable)
        {
            return UpdateOutcome.Unavailable;
        }
        UpdateCheck? update;
        try
        {
            update = await updater.CheckAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Network and feed failures come in many exception types; none may crash a tray app.
            if (interactive)
            {
                host.Marshal(() => dialogs.Inform(CheckFailedMessage, ex.Message));
            }
            return UpdateOutcome.Failed;
        }
        if (update is null)
        {
            if (interactive)
            {
                host.Marshal(() => dialogs.Inform(UpToDateMessage, UpToDateDetail(updater.VersionDisplay)));
            }
            return UpdateOutcome.UpToDate;
        }
        if (!interactive && settings.AutoUpdate)
        {
            // Stage each version once. The 24 h check keeps finding the same pending update
            // until the user quits; downloading it again and queueing a second Update.exe
            // wait buys nothing — the first staging is the one that will run.
            if (StagedVersion == update.Version)
            {
                return UpdateOutcome.StagedForQuit;
            }
            try
            {
                await updater.DownloadAsync(update).ConfigureAwait(false);
                updater.ApplyOnQuit(update);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // IUpdater documents that DownloadAsync throws on network or filesystem
                // failure. A background check must not leave an unobserved exception behind
                // and must stay silent; the next check retries.
                return UpdateOutcome.Failed;
            }
            StagedVersion = update.Version;
            if (NotifiedVersion != update.Version)
            {
                NotifiedVersion = update.Version;
                host.Marshal(() => notifier.Notify(Notifications.Title, ReadyToastBody));
            }
            return UpdateOutcome.StagedForQuit;
        }
        // MarshalAsync, not Marshal-then-read-a-captured-local: AppHost.Marshal only POSTS,
        // so the answer is not there when it returns.
        var install = await host.MarshalAsync(() => dialogs.OfferUpdate(update.Version, updater.VersionDisplay, update.NotesMarkdown)).ConfigureAwait(false);
        if (!install)
        {
            return UpdateOutcome.Deferred;
        }
        try
        {
            await updater.DownloadAsync(update).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The user asked for this and is waiting; CheckFailedMessage is the one failure
            // string the spec gives us, and the exception text says what actually broke.
            host.Marshal(() => dialogs.Inform(CheckFailedMessage, ex.Message));
            return UpdateOutcome.Failed;
        }
        // ApplyAndRestart does not return: Velopack's ApplyUpdatesAndRestart exits the
        // process from inside the call, so App.OnExit never runs. Everything this app owns
        // is already durable at this point (settings persist per setter; watchers need no
        // teardown), which is why there is nothing to flush here.
        host.Marshal(() => updater.ApplyAndRestart(update));
        return UpdateOutcome.Installing;
    }

    public void Dispose() => disposed = true;
}

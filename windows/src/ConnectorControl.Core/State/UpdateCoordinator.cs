using System.Runtime.ExceptionServices;
using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.State;

/// <summary>
/// Checks 10 s after launch and every 24 h; with autoUpdate on,
/// download silently, stage for quit, and toast once per version; otherwise -
/// and always for Check for Updates - show the update dialog. A version the
/// user declines from a background offer is not re-offered by later
/// background checks (Sparkle's Skip This Version is the Mac counterpart);
/// Check for Updates always offers, even a version already declined. Manual
/// checks report "up to date" and failures; background checks stay silent.
/// </summary>
public sealed class UpdateCoordinator : IDisposable
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>The update dialog's window title, as Sparkle titles the Mac's.</summary>
    public const string DialogTitle = "Software Update";
    public const string ReadyToastBody = "An update to Connector Control is ready and will install when you quit.";
    public const string UpToDateMessage = "You're up to date.";
    public const string CheckFailedMessage = "Couldn’t check for updates.";
    /// <summary>A download or relaunch the user asked for with Install and Relaunch failed.</summary>
    public const string InstallFailedMessage = "Couldn’t install the update.";
    public const string UpdateRefusedMessage = "The update was not installed.";
    public const string AvailableHeadline = "A new version of Connector Control is available!";
    public const string InstallButton = "Install and Relaunch";
    public const string LaterButton = "Later";
    /// <summary>The Install and Relaunch button while the package downloads, the dialog still up and busy.</summary>
    public const string DownloadingTitle = "Downloading…";

    public static string UpToDateDetail(string version) => $"Connector Control {version} is currently the newest version available.";

    public static string AvailableDetail(string newVersion, string currentVersion) =>
        $"Connector Control {newVersion} is now available - you have {currentVersion}. Would you like to install it now?";

    private readonly IUpdater updater;
    private readonly ISettings settings;
    private readonly INotifier notifier;
    private readonly IDialogs dialogs;
    private readonly AppHost host;
    private bool started;
    private bool disposed;
    private Task<UpdateOutcome>? inFlightCheck;

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

    /// <summary>The version whose package failed verification and was announced, so the daily check does not toast it again.</summary>
    public string? RefusedVersion { get; private set; }

    /// <summary>
    /// The version the user declined via a non-interactive offer, so the background check does not
    /// re-offer it; Settings ▸ Check for Updates always offers regardless. Backed by
    /// <see cref="ISettings.DeclinedUpdateVersion"/> so a decline survives a relaunch.
    /// </summary>
    public string? DeclinedVersion => settings.DeclinedUpdateVersion;

    /// <summary>
    /// A check is in flight, scheduled or asked for, from its start until its slot is cleared: Check for Updates
    /// reads "Checking…" and cannot be clicked meanwhile, rather than silently joining it. Windows only: Sparkle
    /// shows the Mac's.
    /// </summary>
    public bool IsChecking => inFlightCheck is not null;

    /// <summary>Raised on the UI thread whenever <see cref="IsChecking"/> changes.</summary>
    public event Action? CheckingChanged;

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

    /// <summary><paramref name="interactive"/>: Settings ▸ Check for Updates (always shows a result); false for scheduled checks.</summary>
    public Task<UpdateOutcome> CheckAsync(bool interactive)
    {
        if (!updater.IsAvailable)
        {
            return Task.FromResult(UpdateOutcome.Unavailable);
        }
        // A manual "Check for Updates" can race the scheduled 24 h tick. Join whichever
        // check is already in flight instead of hitting the feed (and possibly the
        // download) a second time; both callers see the same outcome.
        if (inFlightCheck is { } running)
        {
            return running;
        }
        // The completion is reserved in inFlightCheck before RunOutcomeAsync starts (not
        // after RunCheckAsync returns): every fake in this test suite completes
        // synchronously, so a "call it, then store what it returned" ordering would let
        // RunCheckAsync's own finally clear the field before this method's assignment
        // ever ran, resurrecting a stale, already-completed task for the next caller.
        var completion = new TaskCompletionSource<UpdateOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        inFlightCheck = completion.Task;
        CheckingChanged?.Invoke();
        _ = RunCheckAsync(interactive, completion);
        return completion.Task;
    }

    private async Task RunCheckAsync(bool interactive, TaskCompletionSource<UpdateOutcome> completion)
    {
        UpdateOutcome outcome = default;
        Exception? failure = null;
        try
        {
            outcome = await RunOutcomeAsync(interactive).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        // inFlightCheck is UI-thread state (CheckAsync reads and writes it there) and this
        // continuation is on a pool thread, so the clear goes through the host like every
        // other state write in this class - and the outcome is published only AFTER it has
        // run, so a caller resuming on the UI thread never finds the finished task still
        // parked in the field and joins it instead of checking afresh. Installing is the one
        // outcome this never delivers on a real install: ApplyAndRestart, posted just before,
        // exits the process from inside the dispatcher, and both production callers discard
        // the task anyway.
        try
        {
            await host.MarshalAsync(() =>
            {
                inFlightCheck = null;
                CheckingChanged?.Invoke();
                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The dispatcher is gone (app shutdown): nothing can race this clear any more, and
            // leaving the field set would hand every later caller the same finished task.
            inFlightCheck = null;
        }
        if (failure is null)
        {
            completion.SetResult(outcome);
        }
        else
        {
            completion.SetException(failure);
        }
    }

    private async Task<UpdateOutcome> RunOutcomeAsync(bool interactive)
    {
        UpdateCheck? update;
        try
        {
            update = await updater.CheckAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Network and feed failures come in many exception types; none may crash a tray app.
            InformIf(interactive, CheckFailedMessage, ex.Message);
            return UpdateOutcome.Failed;
        }
        if (update is null)
        {
            InformIf(interactive, UpToDateMessage, UpToDateDetail(updater.VersionDisplay));
            return UpdateOutcome.UpToDate;
        }
        if (!interactive && settings.AutoUpdate)
        {
            // Stage each version once. The 24 h check keeps finding the same pending update
            // until the user quits; downloading it again and queueing a second Update.exe
            // wait buys nothing - the first staging is the one that will run.
            if (StagedVersion == update.Version)
            {
                return UpdateOutcome.StagedForQuit;
            }
            try
            {
                await updater.DownloadAsync(update).ConfigureAwait(false);
            }
            catch (UpdateVerificationException refused)
            {
                // The one download failure a silent check must not keep quiet about.
                await host.MarshalAsync(() =>
                {
                    if (RefusedVersion != update.Version)
                    {
                        RefusedVersion = update.Version;
                        notifier.Notify(Notifications.Title, refused.Message);
                    }
                    return true;
                }).ConfigureAwait(false);
                return UpdateOutcome.Failed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // IUpdater documents that DownloadAsync throws on network or filesystem
                // failure. A background check must not leave an unobserved exception behind
                // and must stay silent; the next check retries.
                return UpdateOutcome.Failed;
            }
            // Everything from here on reads/writes state the UI thread owns, so it runs
            // as one marshalled action: on a non-inline AppHost, StagedVersion and
            // NotifiedVersion only change once the host's queue is pumped, same as every
            // other AppState mutation reached from off the UI thread.
            await host.MarshalAsync(() =>
            {
                updater.ApplyOnQuit(update);
                StagedVersion = update.Version;
                if (NotifiedVersion != update.Version)
                {
                    NotifiedVersion = update.Version;
                    notifier.Notify(Notifications.Title, ReadyToastBody);
                }
                return true;
            }).ConfigureAwait(false);
            return UpdateOutcome.StagedForQuit;
        }
        // A background check does not re-offer a version the user already sent Later on;
        // Settings ▸ Check for Updates always offers, declined or not.
        if (!interactive && DeclinedVersion == update.Version)
        {
            return UpdateOutcome.Deferred;
        }
        // MarshalAsync, not Marshal-then-read-a-captured-local: AppHost.Marshal only POSTS,
        // so the answer is not there when it returns. Install and Relaunch's work runs inside the
        // offer, so the dialog stays up, busy, until the app relaunches; a failure ends it, and is
        // reported once the dialog has closed. Cancel, while it runs, returns the dialog to its offer
        // with nothing to report; the next Install and Relaunch starts afresh.
        Exception? installFailure = null;
        async Task InstallAsync(CancellationToken cancellationToken)
        {
            installFailure = null;
            try
            {
                // The dialog calls this on the UI thread, and the updater checks signatures before its first
                // await, so the download starts on the pool, as it did before the dialog waited for it. The
                // token goes to the download only: given to Task.Run, a Cancel already pressed would skip it.
                await Task.Run(() => updater.DownloadAsync(update, cancellationToken)).ConfigureAwait(false);
                // ApplyAndRestart does not return: Velopack's ApplyUpdatesAndRestart exits the
                // process from inside the call, so App.OnExit never runs. Everything this app owns
                // is already durable at this point (settings persist per setter; watchers need no
                // teardown), which is why there is nothing to flush here.
                await host.MarshalAsync(() =>
                {
                    // A Cancel that came once the download had finished still stops the relaunch.
                    cancellationToken.ThrowIfCancellationRequested();
                    updater.ApplyAndRestart(update);
                    return true;
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The user's Cancel: nothing failed, and the dialog is back at its offer.
            }
            catch (Exception ex)
            {
                // The dialog awaits this from a click handler, where an exception would crash the app.
                installFailure = ex;
            }
        }
        bool install;
        try
        {
            install = await host.MarshalAsync(() => dialogs.OfferUpdate(update.Version, updater.VersionDisplay, update.NotesMarkdown, InstallAsync))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            InformIf(interactive, CheckFailedMessage, ex.Message);
            return UpdateOutcome.Failed;
        }
        if (!install)
        {
            if (!interactive)
            {
                await host.MarshalAsync(() =>
                {
                    settings.DeclinedUpdateVersion = update.Version;
                    return true;
                }).ConfigureAwait(false);
            }
            return UpdateOutcome.Deferred;
        }
        switch (installFailure)
        {
            case null:
                return UpdateOutcome.Installing;
            case OperationCanceledException:
                ExceptionDispatchInfo.Capture(installFailure).Throw();
                return UpdateOutcome.Failed;   // not reached
            case UpdateVerificationException refused:
                host.Marshal(() => dialogs.Inform(UpdateRefusedMessage, refused.Message));
                return UpdateOutcome.Failed;
            default:
                // The user asked for this and is waiting: the download or the relaunch failed, and the
                // exception text says what actually broke.
                host.Marshal(() => dialogs.Inform(InstallFailedMessage, installFailure.Message));
                return UpdateOutcome.Failed;
        }
    }

    /// <summary>A background check stays silent; only an interactive one (Check for Updates) shows the result.</summary>
    private void InformIf(bool interactive, string message, string detail)
    {
        if (interactive)
        {
            host.Marshal(() => dialogs.Inform(message, detail));
        }
    }

    public void Dispose() => disposed = true;
}

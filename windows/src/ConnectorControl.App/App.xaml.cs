using System.Windows;
using System.Windows.Threading;
using ConnectorControl.App.Services;
using ConnectorControl.App.Tray;
using ConnectorControl.App.Views;
using ConnectorControl.Core;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.App;

/// <summary>
/// Composition root: Velopack hook first,
/// single-instance guard, platform services, AppState (which reloads and arms
/// watchers), the update coordinator, the control layer Claude and the
/// command-line tool reach, windows, tray icon, first-run tip.
/// No window is shown at startup; the tray icon is the app.
/// </summary>
public partial class App : Application
{
    private const string LogFileName = "crash.log";
    private static readonly Lock LogGate = new();

    private SingleInstance? instance;
    private PlatformServices? services;
    private AppState? state;
    private UpdateCoordinator? updates;
    private FlyoutModel? flyoutModel;
    private FlyoutWindow? flyout;
    private TrayController? tray;
    private SettingsModel? controlSettings;
    private ControlServer? controlServer;
    private ControlPipeServer? controlEndpoint;

    /// <summary>Puts the tool on PATH for terminal use; the Settings window binds it.</summary>
    internal ICommandLineTool? CommandLineTool { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        VelopackUpdater.RunStartupHook();   // must run before anything else (install/update/uninstall callbacks)
        base.OnStartup(e);
        if (PackageVerificationCommand.TryRun(e.Args, out var verificationExitCode))
        {
            // smoke-test.ps1's own invocation of the installed exe: check a package and exit,
            // never reaching the tray UI or the single-instance guard.
            Shutdown(verificationExitCode);
            return;
        }
        DispatcherUnhandledException += OnUnhandledException;   // before the second-instance path too, so it leaves a crash.log
        instance = new SingleInstance();
        if (!instance.IsFirstInstance)
        {
            // A user double-clicking the shortcut of a running tray app must see something;
            // Windows relaunching us to deliver a toast click must not (the toolkit's COM
            // server already routed it to the running process), and nor must the command-line
            // tool starting an app that was already starting: it asked only for the app to run.
            // The uninstall hook relies on this: it holds the lock, so an app Claude's relay starts
            // while it runs ends here, before it can put back the entry the hook takes away.
            if (!SingleInstance.IsToastActivation(e.Args) && !SingleInstance.IsBackgroundLaunch(e.Args))
            {
                instance.SignalShow();
            }
            Shutdown();
            return;
        }

        var dataDir = ServiceFactory.DefaultDataDir;
        // Claude's entry runs the copy of the tool outside Velopack's update folder, which an update
        // rewrites, ending whatever runs from it. The copy is refreshed off the UI thread; its path never
        // changes, so the entry names it whatever the refresh finds.
        var appExecutable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ControlEndpoint.AppFileName);
        _ = Task.Run(() =>
        {
            // The task's boundary: nothing awaits it, so whatever goes wrong is logged here or never seen. A copy
            // that failed leaves Claude's entry naming a tool that may not be there.
            try
            {
                ControlToolCopy.Refresh(Path.Combine(AppContext.BaseDirectory, ControlEndpoint.ToolFileName), dataDir, appExecutable, failed: Log);
            }
            catch (Exception ex)
            {
                Log($"The command-line tool's copy could not be refreshed: {ex}");
            }
        });
        var host = UiThread.Host(ControlToolCopy.ToolPath(dataDir));
        // CreateDefault performs the toast COM registration, so it must precede any Notify.
        services = ServiceFactory.CreateDefault(host.Marshal);
        // The startup half of the updater's recovery: an Update.exe an earlier check left in place is put back before
        // anything can run it. Here, the first moment the updater exists, rather than straight after the startup hook:
        // only the app's one instance may do it, or a second launch could undo a check the first is running.
        // Off the UI thread: the installed-updater check reads signatures and may wait on revocation. Every update check
        // waits for it, so it cannot sweep packages\ under a download.
        (services.Updater as VelopackUpdater)?.StartRecovery();
        // No owner: WpfDialogs falls back to whichever of our windows is active, so a dialog
        // raised from Settings still centres on Settings.
        var dialogs = new WpfDialogs(() => null);
        var paths = PathContext.Live();
        state = new AppState(services.Settings, services.ClaudeProcess, services.Notifier, dialogs, paths, host, new ToolProbe(paths.Environment));
        state.QuitRequested += () => Shutdown();
        updates = new UpdateCoordinator(services.Updater, services.Settings, services.Notifier, dialogs, host);
        updates.Start();   // only arms the delayed first check through host.Delay
        StartControl(state, services, updates, host, paths, dataDir, dialogs);
        var windows = new WindowRegistry(state, services, updates, dialogs, () => CommandLineTool);
        flyoutModel = new FlyoutModel(state, services.Settings);
        flyout = new FlyoutWindow(flyoutModel, windows);
        tray = new TrayController(state, flyout, windows);
        instance.OnShowRequested(() => host.Marshal(() => flyout.ShowFlyout()));
        FirstRunTip.ShowIfNeeded(services.Settings, services.Notifier);
    }

    /// <summary>The control layer: the one ControlTools every connection shares, the server that gives each its
    /// own session, and the pipe it listens on.</summary>
    private void StartControl(AppState appState, PlatformServices platform, UpdateCoordinator coordinator, AppHost host, PathContext paths, string dataDir,
                              IDialogs dialogs)
    {
        controlSettings = new SettingsModel(appState, platform.Settings, platform.Autostart, platform.ClaudeInstall, platform.Updater, coordinator);
        // Claude's gated changes are asked about in the app's own dialog. WPF's nested frame runs the
        // dispatcher's queue whatever called ShowDialog, which is what keeps the call's progress and early
        // answer going out while it is open; posting the dialog to a turn of its own is for parity with the
        // Mac, whose alert must not be entered inside the block that asked.
        var tools = new ControlTools(appState, controlSettings, new DialogConfirmer(dialogs, UiThread.Post), host);
        controlServer = new ControlServer(tools);
        try
        {
            controlEndpoint = ControlPipeServer.Start(ControlEndpoint.PipeName(paths.Environment, ControlEndpoint.CurrentUserSid()),
                controlServer, host, stopped: () =>
                {
                    Log("The control pipe stopped serving.");
                    appState.ReportControlEndpointUnavailable();
                });
        }
        catch (Exception ex)
        {
            // Another copy of the app serves the pipe, another account holds its name, or anything else keeps it
            // from being served: the app runs without one, and says so, as the Mac does for any failure.
            Log($"The control pipe is unavailable: {ex.GetType().Name}: {ex.Message}");
            appState.ReportControlEndpointUnavailable();
        }
        CommandLineTool = new UserPathCommandLineTool(ControlToolCopy.BinDir(dataDir), new RegistryUserEnvironment(), host);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // The pipe first, then every connection, both on the UI thread: no connection opens once the
        // server has closed them all.
        controlEndpoint?.Dispose();
        controlServer?.CloseAll();
        controlSettings?.Dispose();
        tray?.Dispose();
        updates?.Dispose();
        flyoutModel?.Dispose();
        state?.Dispose();
        services?.Dispose();   // the Services record owns and disposes the toast notifier
        instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>A tray app that dies silently is indistinguishable from one that never started: leave a note, then let it die.</summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) => Log(e.Exception.ToString());

    /// <summary>
    /// The app's one durable log, crash.log in its data folder: a crash, and the few failures that would
    /// otherwise go unseen - a tray app has no console, and Trace reaches only a debugger. The Mac's
    /// counterpart is NSLog, which the system keeps. One line each, stamped in UTC, from any thread.
    /// </summary>
    internal static void Log(string message)
    {
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(ServiceFactory.DefaultDataDir);
                File.AppendAllText(Path.Combine(ServiceFactory.DefaultDataDir, LogFileName), $"{DateTime.UtcNow:o} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (FileSystemErrors.IsTransient(ex))
        {
            // nothing more to do
        }
    }
}

using System.Windows;
using System.Windows.Threading;
using ConnectorControl.App.Services;
using ConnectorControl.App.Tray;
using ConnectorControl.App.Views;
using ConnectorControl.Core.State;
using AppServices = ConnectorControl.App.Services.Services;

namespace ConnectorControl.App;

/// <summary>
/// Composition root (catalog §0 / spec §7.6 init order): Velopack hook first,
/// single-instance guard, platform services, AppState (which reloads and arms
/// watchers), the update coordinator, windows, tray icon, first-run tip.
/// No window is shown at startup; the tray icon is the app.
/// </summary>
public partial class App : Application
{
    private SingleInstance? instance;
    private AppServices? services;
    private AppState? state;
    private UpdateCoordinator? updates;
    private FlyoutModel? flyoutModel;
    private FlyoutWindow? flyout;
    private TrayController? tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        VelopackUpdater.RunStartupHook();   // must run before anything else (install/update/uninstall callbacks)
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;   // before the second-instance path too, so it leaves a crash.log
        instance = new SingleInstance();
        if (!instance.IsFirstInstance)
        {
            // A user double-clicking the shortcut of a running tray app must see something;
            // Windows relaunching us to deliver a toast click must not (the toolkit's COM
            // server already routed it to the running process).
            if (!SingleInstance.IsToastActivation(e.Args))
            {
                instance.SignalShow();
            }
            Shutdown();
            return;
        }

        var host = UiThread.Host();
        // CreateDefault performs the toast COM registration, so it must precede any Notify.
        services = ServiceFactory.CreateDefault(host.Marshal);
        // No owner: WpfDialogs falls back to whichever of our windows is active, so a dialog
        // raised from Settings still centres on Settings.
        var dialogs = new WpfDialogs(() => null);
        state = new AppState(services.Settings, services.ClaudeProcess, services.Notifier, dialogs, PathContext.Live(), host);
        state.QuitRequested += () => Shutdown();
        updates = new UpdateCoordinator(services.Updater, services.Settings, services.Notifier, dialogs, host);
        updates.Start();   // only arms the delayed first check through host.Delay
        var windows = new WindowRegistry(state, services, updates);
        flyoutModel = new FlyoutModel(state);
        flyout = new FlyoutWindow(flyoutModel, windows);
        tray = new TrayController(state, flyout, windows);
        instance.OnShowRequested(() => host.Marshal(() => flyout.ShowFlyout()));
        FirstRunTip.ShowIfNeeded(services.Settings, services.Notifier);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        updates?.Dispose();
        flyoutModel?.Dispose();
        state?.Dispose();
        services?.Dispose();   // the Services record owns and disposes the toast notifier
        instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>A tray app that dies silently is indistinguishable from one that never started: leave a note, then let it die.</summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ServiceFactory.DefaultDataDir);
            File.AppendAllText(Path.Combine(ServiceFactory.DefaultDataDir, "crash.log"), $"{DateTime.UtcNow:o} {e.Exception}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // nothing more to do
        }
    }
}

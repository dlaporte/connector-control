using ConnectorControl.Core.Services;

namespace ConnectorControl.App.Services;

/// <summary>
/// Everything AppState (Phase 3) needs from the platform. Disposing it disposes
/// the services it owns, which today means the notifier: ToastNotifier holds a
/// toast-activation subscription. App.OnExit disposes this one object.
/// </summary>
public sealed record Services(
    ISettings Settings,
    IClaudeInstall ClaudeInstall,
    IClaudeProcess ClaudeProcess,
    INotifier Notifier,
    IAutostart Autostart,
    IUpdater Updater) : IDisposable
{
    public void Dispose() => (Notifier as IDisposable)?.Dispose();
}

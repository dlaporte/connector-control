using ConnectorControl.Core.Services;

namespace ConnectorControl.App.Services;

/// <summary>Everything AppState (Phase 3) needs from the platform.</summary>
public sealed record Services(
    ISettings Settings,
    IClaudeInstall ClaudeInstall,
    IClaudeProcess ClaudeProcess,
    INotifier Notifier,
    IAutostart Autostart,
    IUpdater Updater);

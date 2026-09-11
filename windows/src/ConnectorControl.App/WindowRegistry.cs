using System.Windows;
using ConnectorControl.App.Services;
using ConnectorControl.App.Views;
using ConnectorControl.Core.State;

namespace ConnectorControl.App;

/// <summary>
/// One editor window per target id (an existing connector's id is
/// its name; a new one gets a fresh GUID each time), brought forward if
/// already open; one Settings window.
/// </summary>
public sealed class WindowRegistry
{
    private readonly AppState state;
    private readonly PlatformServices services;
    private readonly UpdateCoordinator updates;
    private readonly Dictionary<string, EditorWindow> editors = new(StringComparer.Ordinal);
    private SettingsWindow? settings;

    public WindowRegistry(AppState state, PlatformServices services, UpdateCoordinator updates)
    {
        this.state = state;
        this.services = services;
        this.updates = updates;
    }

    public void OpenEditor(EditTarget target)
    {
        if (editors.TryGetValue(target.Id, out var open))
        {
            BringToFront(open);
            return;
        }
        var window = new EditorWindow(state, target);
        editors[target.Id] = window;
        window.Closed += (_, _) => editors.Remove(target.Id);
        window.Show();
        BringToFront(window);
    }

    public void OpenSettings()
    {
        if (settings is null)
        {
            settings = new SettingsWindow(state, services, updates);
            settings.Closed += (_, _) => settings = null;
            settings.Show();
        }
        BringToFront(settings);
    }

    /// <summary>The Mac's NSApp.activate(ignoringOtherApps:): a tray app has no foreground window to inherit activation from.</summary>
    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.Show();
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }
}

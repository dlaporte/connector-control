using System.Windows;
using ConnectorControl.App.Services;
using ConnectorControl.App.Views;
using ConnectorControl.Core.State;

namespace ConnectorControl.App;

/// <summary>
/// One editor window per target id, brought forward if already open: an existing connector's id
/// is its collection, U+001F, then its name (<see cref="EditTarget.Id"/>), so it is one editor per
/// connector per collection, and a new one gets a fresh GUID each time. One Settings window, and
/// one Collections window.
/// </summary>
public sealed class WindowRegistry
{
    private readonly AppState state;
    private readonly PlatformServices services;
    private readonly UpdateCoordinator updates;
    private readonly Dictionary<string, EditorWindow> editors = new(StringComparer.Ordinal);
    private readonly IDialogs dialogs;
    private readonly Func<ICommandLineTool?> commandLineTool;
    private SettingsWindow? settings;
    private CollectionsWindow? collections;

    /// <summary>
    /// <paramref name="dialogs"/> is what the Collections window's model asks its questions
    /// through — AppState keeps its own copy private, so App hands over the ownerless instance it
    /// built for AppState, and a test hands over its fake. <paramref name="commandLineTool"/> gives the tool
    /// Settings' Command-line tool group installs and removes, asked for each time Settings opens, so it does
    /// not matter whether App made the tool before or after this registry.
    /// </summary>
    public WindowRegistry(AppState state, PlatformServices services, UpdateCoordinator updates, IDialogs dialogs,
                          Func<ICommandLineTool?>? commandLineTool = null)
    {
        this.state = state;
        this.services = services;
        this.updates = updates;
        this.dialogs = dialogs;
        this.commandLineTool = commandLineTool ?? (() => null);
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
            settings = new SettingsWindow(state, services, updates, commandLineTool());
            settings.Closed += (_, _) => settings = null;
            settings.Show();
        }
        BringToFront(settings);
    }

    /// <summary>
    /// One Collections window, re-activated rather than reopened. What the flyout wants of it —
    /// a dialog in front of it the moment it appears — travels through
    /// <see cref="AppState.CollectionsWindowRequest"/>, which the window reads on load and on
    /// every change, so opening it takes no arguments.
    /// </summary>
    public void OpenCollections()
    {
        if (collections is null)
        {
            collections = new CollectionsWindow(state, this, dialogs);
            collections.Closed += (_, _) => collections = null;
            collections.Show();
        }
        BringToFront(collections);
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

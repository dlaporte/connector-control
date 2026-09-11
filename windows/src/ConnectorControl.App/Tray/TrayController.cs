using System.ComponentModel;
using System.Windows.Controls;
using ConnectorControl.App.Views;
using ConnectorControl.Core.State;
using H.NotifyIcon;
using Microsoft.Win32;

namespace ConnectorControl.App.Tray;

/// <summary>
/// Spec §7.1: the tray icon (powerplug, or the warning triangle while an apply
/// awaits retry; black on a light taskbar, white on a dark one, re-rendered on
/// theme change), left-click toggles the flyout, right-click shows Open /
/// Settings… / Quit Connector Control.
/// </summary>
public sealed class TrayController : IDisposable
{
    public const string ToolTip = "Connector Control";

    private readonly TaskbarIcon icon = new();
    private readonly AppState state;
    private TrayGlyph? currentGlyph;
    private bool currentLight;
    private int currentSize;

    public TrayController(AppState state, FlyoutWindow flyout, WindowRegistry windows)
    {
        this.state = state;
        icon.ToolTipText = ToolTip;
        icon.NoLeftClickDelay = true;
        icon.ContextMenu = BuildMenu(flyout.ShowFlyout, windows.OpenSettings, state.QuitApp);   // Open goes through ShowFlyout, so it anchors on the tray like a left-click (spec §7.1)
        icon.TrayLeftMouseUp += (_, _) => flyout.Toggle();
        RefreshTheme();
        icon.ForceCreate(false);
        state.PropertyChanged += OnStateChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>Spec §7.1 right-click menu.</summary>
    internal static ContextMenu BuildMenu(Action open, Action settings, Action quit)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Open", open));
        menu.Items.Add(Item("Settings…", settings));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit Connector Control", quit));
        return menu;
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ObservableObject.Affects(e, nameof(AppState.ApplyRetryNeeded)))
        {
            // A state tick never touches theme or DPI — only the glyph can have changed.
            Render(GlyphFor(state));
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.VisualStyle)
        {
            RefreshTheme();
        }
    }

    private static TrayGlyph GlyphFor(AppState state) => state.ApplyRetryNeeded ? TrayGlyph.Warning : TrayGlyph.Plug;

    /// <summary>Theme and DPI are read here and in the constructor only — reading the registry and
    /// calling GetDpiForSystem on every AppState tick was pure waste for a value that rarely moves.</summary>
    private void RefreshTheme()
    {
        currentLight = TaskbarTheme.IsLight();
        // The pixel size is part of the key: a display-scaling change must re-render, or the
        // shell keeps upscaling a stale 16 px bitmap (Task 13 review).
        currentSize = TrayIconRenderer.SystemIconPixelSize();
        currentGlyph = null;   // force the render below even if the glyph itself didn't change
        Render(GlyphFor(state));
    }

    private void Render(TrayGlyph glyph)
    {
        if (glyph == currentGlyph)
        {
            return;
        }
        currentGlyph = glyph;
        icon.Icon = TrayIconRenderer.RenderIcon(glyph, currentLight, currentSize);
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        state.PropertyChanged -= OnStateChanged;
        icon.Dispose();
    }
}

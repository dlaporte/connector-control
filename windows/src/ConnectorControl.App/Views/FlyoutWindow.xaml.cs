using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ConnectorControl.Core.State;
using H.NotifyIcon.Core;
using H.NotifyIcon.Interop;
using DrawingPoint = System.Drawing.Point;

namespace ConnectorControl.App.Views;

/// <summary>
/// Spec §7.1: the Mac popover as a borderless, topmost, taskbar-less window
/// sized to content (240–380 wide), rounded on Windows 11, anchored beside the
/// notification area, closed on deactivate or Escape, reloading on every open.
/// </summary>
public partial class FlyoutWindow : Window
{
    /// <summary>Clicking the tray icon deactivates (hides) an open flyout before the click arrives; ignore that click so it toggles instead of reopening.</summary>
    public static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(300);

    private readonly FlyoutModel model;
    private readonly WindowRegistry windows;
    private ContextMenu? openMenu;

    public FlyoutWindow(FlyoutModel model, WindowRegistry windows)
    {
        InitializeComponent();
        this.model = model;
        this.windows = windows;
        DataContext = model;
        Deactivated += (_, _) => HandleDeactivated();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public DateTime LastHiddenUtc { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// Where the flyout anchors, in physical pixels. TrayInfo.GetTrayLocation reads
    /// SHAppBarMessage(ABM_GETTASKBARPOS) and throws when the shell is not there;
    /// TaskbarIcon.GetPopupTrayPosition() is the same value scaled to DIPs for a WPF
    /// Popup, which SetWindowPos would misread. Null means "ask the cursor instead".
    /// Settable so a test can pin the anchor without a shell.
    /// </summary>
    public Func<DrawingPoint?> TrayAnchor { get; set; } = ShellTrayAnchor;

    private static DrawingPoint? ShellTrayAnchor()
    {
        try
        {
            return TrayInfo.GetTrayLocation();
        }
        catch (InvalidOperationException)
        {
            return null;   // Explorer restarting, or no shell at all
        }
    }

    /// <summary>True while a menu this window owns is on screen (the profile chip's).</summary>
    internal bool HasOpenPopup => openMenu is { IsOpen: true };

    public void Toggle()
    {
        if (IsVisible)
        {
            HideFlyout();
        }
        else if (DateTime.UtcNow - LastHiddenUtc > ReopenGuard)
        {
            ShowFlyout();
        }
    }

    /// <summary>Reload (the Mac onAppear), show off-screen so SizeToContent settles, then move next to the cursor and activate.</summary>
    public void ShowFlyout()
    {
        model.Opened();
        Show();
        UpdateLayout();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
        {
            WindowPlacement.MoveNearTray(hwnd, TrayAnchor());
        }
        Activate();
    }

    public void HideFlyout()
    {
        if (!IsVisible)
        {
            return;
        }
        LastHiddenUtc = DateTime.UtcNow;
        Hide();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DesktopWindowsManagerMethods.SetRoundedCorners(new WindowInteropHelper(this).Handle);   // no-op before Windows 11
    }

    /// <summary>The flyout is never destroyed while the app runs; closing hides it.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (Application.Current is { } app && !app.Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            HideFlyout();
        }
        base.OnClosing(e);
    }

    /// <summary>
    /// Deactivation normally dismisses the flyout — but a WPF ContextMenu lives in its
    /// own top-level window, so opening the profile chip's menu deactivates us, and
    /// hiding here would take the menu's PlacementTarget away with it and leave profiles
    /// unreachable (catalog §2.2: the chip menu is the only way to switch, create,
    /// rename or delete a profile). Ignore those; the check is repeated once the menu
    /// closes. Internal so a test can raise it without a real focus change.
    /// </summary>
    internal void HandleDeactivated()
    {
        if (HasOpenPopup)
        {
            return;
        }
        HideFlyout();
    }

    /// <summary>After an owned menu closes: hide unless the flyout still has the user's attention.</summary>
    internal void HideIfInactive()
    {
        if (HasOpenPopup || IsActive || IsKeyboardFocusWithin)
        {
            return;
        }
        HideFlyout();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideFlyout();
            e.Handled = true;
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        windows.OpenEditor(EditTarget.NewRemote(EditorWindow.NewRemoteStyle));
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        windows.OpenSettings();
    }

    private void OnQuit(object sender, RoutedEventArgs e) => model.Quit();

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ConnectorRow row || model.EntryFor(row.Name) is not { } entry)
        {
            return;   // the entry vanished since the row was drawn
        }
        HideFlyout();
        windows.OpenEditor(EditTarget.Existing(row.Name, entry));
    }

    private void OnFooter(object sender, RoutedEventArgs e) => model.FooterAction();

    private void OnProfileChip(object sender, RoutedEventArgs e) => OpenProfileMenu();

    /// <summary>Catalog §2.2 profile chip menu: profiles (check on the active), separator, New / Rename / Delete.</summary>
    internal ContextMenu OpenProfileMenu()
    {
        var menu = new ContextMenu { PlacementTarget = ProfileChip, Placement = PlacementMode.Bottom, StaysOpen = false };
        foreach (var item in model.ProfileItems)
        {
            var name = item.Name;
            var entry = new MenuItem { Header = new TextBlock { Text = name }, IsChecked = item.IsActive };
            entry.Click += (_, _) => model.SwitchProfile(name);
            menu.Items.Add(entry);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor(FlyoutModel.NewProfileTitle, model.NewProfile));
        menu.Items.Add(MenuItemFor(model.RenameProfileTitle, model.RenameProfile));
        var delete = MenuItemFor(model.DeleteProfileTitle, model.DeleteProfile);
        delete.IsEnabled = model.CanDeleteProfile;
        menu.Items.Add(delete);
        // The reference, not an Opened/Closed counter: ContextMenu.Closed can be deferred by
        // the menu's fade animation, and HasOpenPopup must never be wrong in the meantime.
        openMenu = menu;
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(new Action(HideIfInactive), DispatcherPriority.Background);
        menu.IsOpen = true;
        return menu;
    }

    private static MenuItem MenuItemFor(string title, Action action)
    {
        var item = new MenuItem { Header = new TextBlock { Text = title } };
        item.Click += (_, _) => action();
        return item;
    }
}

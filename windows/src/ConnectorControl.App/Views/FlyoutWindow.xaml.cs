using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ConnectorControl.Core.State;
using H.NotifyIcon.Core;
using H.NotifyIcon.Interop;
using Microsoft.Win32;
using DrawingPoint = System.Drawing.Point;
// Named rather than imported: System.Windows.Shapes.Path would collide with System.IO.Path.
using Ellipse = System.Windows.Shapes.Ellipse;

namespace ConnectorControl.App.Views;

/// <summary>
/// The Mac popover as a borderless, topmost, taskbar-less window
/// sized to content (240–380 wide), rounded on Windows 11, anchored beside the
/// notification area, closed on deactivate or Escape, reloading on every open.
/// </summary>
public partial class FlyoutWindow : Window
{
    /// <summary>Clicking the tray icon deactivates (hides) an open flyout before the click arrives; ignore that click so it toggles instead of reopening.</summary>
    public static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(300);

    /// <summary>Where the window sits before <see cref="ShowFlyout"/> repositions it, so SizeToContent settles unseen.</summary>
    public const double OffScreen = -10000;

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

    /// <summary>
    /// The two pickers this window puts in front of itself, and the one dialog a refusal ends
    /// in. One overridable bundle, because a test drives this window on the very dispatcher it
    /// lives on: a real modal would block the test that opened it, and a real picker would wait
    /// for a person.
    /// </summary>
    internal sealed record Presenters(
        Func<string?> ChooseDocument,
        Func<string?> ChooseFolder,
        Action<string> Inform);

    internal static Presenters Live { get; } = new(PickDocument, PickFolder, Tell);

    internal Presenters Surfaces { get; set; } = Live;

    public DateTime LastHiddenUtc { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// Where the flyout anchors, in physical pixels. TrayInfo.GetTrayLocation reads
    /// SHAppBarMessage(ABM_GETTASKBARPOS) and throws when the shell is not there;
    /// TaskbarIcon.GetPopupTrayPosition() is the same value scaled to DIPs for a WPF
    /// Popup, which SetWindowPos would misread. Null means "ask the cursor instead".
    /// Settable so a test can pin the anchor without a shell.
    /// </summary>
    public Func<DrawingPoint?> TrayAnchor { get; internal set; } = ShellTrayAnchor;

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

    /// <summary>True while a menu this window owns is on screen (the collection chip's).</summary>
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
    /// own top-level window, so opening the collection chip's menu deactivates us, and
    /// hiding here would take the menu's PlacementTarget away with it and leave collections
    /// unreachable — the chip menu is the only way to switch, create, rename or delete a
    /// collection. Ignore those; the check is repeated once the menu
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

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        windows.OpenSettings();
    }

    private void OnQuit(object sender, RoutedEventArgs e) => model.Quit();

    private void OnFooter(object sender, RoutedEventArgs e) => model.FooterAction();

    private void OnCollectionChip(object sender, RoutedEventArgs e) => OpenCollectionMenu();

    /// <summary>
    /// The collection chip menu: the collections to switch between with a check on the active
    /// one, then the window that owns everything else — creating, renaming, deleting, and the
    /// document commands included. Built without being shown, so a test can read it.
    /// </summary>
    internal ContextMenu BuildCollectionMenu()
    {
        var menu = new ContextMenu { PlacementTarget = CollectionChip, Placement = PlacementMode.Bottom, StaysOpen = false };
        foreach (var item in model.CollectionItems)
        {
            var name = item.Name;
            // IsCheckable, not just IsChecked: the Fluent MenuItem template gives an item its check
            // column only when it is checkable, so the active collection's mark would not be drawn.
            // Clicking toggles the mark before Click runs, which is harmless — the menu closes and
            // the next open rebuilds every item from CollectionItems.
            var entry = new MenuItem { Header = MenuHeader(item), IsCheckable = true, IsChecked = item.IsActive };
            // A header built from elements gives the item no name of its own to announce, so it
            // is given the model's title — the one the Mac draws, where the pending update is
            // words — and a row reads the same whether it is seen or heard.
            AutomationProperties.SetName(entry, FlyoutModel.MenuTitle(item));
            // The chain's tooltip is out of a screen reader's reach inside the header, so the
            // item carries where the document is as its help text.
            if (FlyoutModel.MenuTooltip(item) is { } source)
            {
                AutomationProperties.SetHelpText(entry, source);
            }
            // Choosing the collection already in front of the user is not a change: switching to
            // it would save and apply it again for nothing, and clear an error banner on the way.
            if (!item.IsActive)
            {
                entry.Click += (_, _) => model.SwitchCollection(name);
            }
            menu.Items.Add(entry);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Command(FlyoutModel.ManageTitle));
        return menu;
    }

    internal ContextMenu OpenCollectionMenu()
    {
        var menu = BuildCollectionMenu();
        // The reference, not an Opened/Closed counter: ContextMenu.Closed can be deferred by
        // the menu's fade animation, and HasOpenPopup must never be wrong in the meantime.
        openMenu = menu;
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(new Action(HideIfInactive), DispatcherPriority.Background);
        menu.IsOpen = true;
        return menu;
    }

    /// <summary>
    /// One menu item that ends in the Collections window. What it wants open in front of that
    /// window is asked for first, because the window reads the request as it appears.
    /// </summary>
    private MenuItem Command(string header, Action? request = null)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            request?.Invoke();
            OpenCollections();
        };
        return item;
    }

    /// <summary>
    /// One collection's row in the menu: its name, then the same two marks the chip carries for
    /// the collection it is showing — a chain for a synced one, an amber dot for one with news.
    /// </summary>
    private StackPanel MenuHeader(CollectionMenuItem item)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = item.Name, VerticalAlignment = VerticalAlignment.Center });
        if (item.IsSynced)
        {
            var chain = new TextBlock
            {
                Text = (string)FindResource("ChainGlyph"),
                FontFamily = (FontFamily)FindResource("IconFont"),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Speak(chain, FlyoutModel.MenuTooltip(item));
            header.Children.Add(chain);
        }
        if (item.HasPendingUpdate)
        {
            var dot = new Ellipse { Style = (Style)FindResource("PendingDot"), Margin = new Thickness(6, 0, 0, 0) };
            Speak(dot, FlyoutModel.PendingSpokenLabel);
            header.Children.Add(dot);
        }
        return header;
    }

    /// <summary>A mark that would otherwise read as nothing, given the sentence it stands for.</summary>
    private static void Speak(FrameworkElement mark, string? sentence)
    {
        if (sentence is null)
        {
            return;
        }
        mark.ToolTip = sentence;
        AutomationProperties.SetName(mark, sentence);
    }

    /// <summary>
    /// The collection banner's first button. True says the news needs nothing from the file
    /// system and the Collections window is the whole answer; false says this window owes a
    /// picker, and which one is what the banner is — the model then refuses anything that is not
    /// what it asked for.
    /// </summary>
    private void OnCollectionBanner(object sender, RoutedEventArgs e)
    {
        if (model.CollectionBannerAction())
        {
            OpenCollections();
            return;
        }
        switch (model.CollectionBanner)
        {
            case CollectionBanner.Locate:
                HideFlyout();
                if (Surfaces.ChooseDocument() is { } path)
                {
                    Report(model.LocateSource(path));
                }
                break;
            case CollectionBanner.PublishFailed:
                HideFlyout();
                if (Surfaces.ChooseFolder() is { } folder)
                {
                    Report(model.ChoosePublishFolder(folder));
                }
                break;
        }
    }

    /// <summary>Stop Publishing, the one banner answer that asks nothing of the user first.</summary>
    private void OnCollectionBannerSecondary(object sender, RoutedEventArgs e) => model.CollectionBannerSecondaryAction();

    /// <summary>
    /// The Collections window, which takes no arguments: whatever the flyout wants in front of it
    /// travels as a request, which the window reads on load and on every change.
    /// </summary>
    private void OpenCollections()
    {
        HideFlyout();
        windows.OpenCollections();
    }

    private void Report(string? failure)
    {
        if (failure is not null)
        {
            Surfaces.Inform(failure);
        }
    }

    /// <summary>The Collections window's picker, for the document a collection asks to be pointed at.</summary>
    private static string? PickDocument()
    {
        var picker = new OpenFileDialog { Title = "Choose", Filter = "Collection (*.json)|*.json", Multiselect = false };
        return picker.ShowDialog() == true ? picker.FileName : null;
    }

    /// <summary>Settings ▸ Storage's picker, for the folder a failed publish asks to be pointed at.</summary>
    private static string? PickFolder()
    {
        var picker = new OpenFolderDialog { Title = "Choose", Multiselect = false };
        return picker.ShowDialog() == true ? picker.FolderName : null;
    }

    /// <summary>
    /// A refusal, said the way every dialog the flyout raises is said: ownerless. Owning one to
    /// this window would own it to a window that hides itself the moment the dialog takes the
    /// focus, which is what <see cref="WpfDialogs.ResolveOwner"/> is written to avoid.
    /// </summary>
    private static void Tell(string message) => new WpfDialogs(() => null).Inform(message, null);
}

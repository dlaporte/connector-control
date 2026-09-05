using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ConnectorControl.App.Tests.TestSupport;
using ConnectorControl.App.Tray;
using ConnectorControl.App.Views;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;
using AppServices = ConnectorControl.App.Services.Services;

namespace ConnectorControl.App.Tests;

public class FlyoutWindowTests
{
    private static void Layout(Window window)
    {
        // WPF defers a binding's first target update to DataBind priority; the test host runs
        // the body synchronously, so pump that queue before reading any bound state (the
        // pattern in EditorWindowTests.Layout).
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        window.Measure(new Size(380, 800));
        window.Arrange(new Rect(0, 0, 380, 800));
        window.UpdateLayout();
    }

    [Fact]
    public void FlyoutShowsHeaderRowsAndNoFooterWhenNothingIsPending()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var services = new AppServices(h.Settings, new FakeClaudeInstall(), h.Claude, h.Notifier, new FakeAutostart(), new FakeUpdater());
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            using var model = new FlyoutModel(state);
            var window = new FlyoutWindow(model, new WindowRegistry(state, services, updates));
            Layout(window);
            Assert.Equal(3, window.RowList.Items.Count);
            Assert.Equal(Visibility.Collapsed, window.FooterPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, window.ErrorBanner.Visibility);
            Assert.Equal(Visibility.Collapsed, window.EmptyLabel.Visibility);
            Assert.Equal("3 of 3 enabled", window.SubtitleText.Text);
            Assert.Equal("Default ▾", window.ProfileChip.Content);
            Assert.False(window.ShowInTaskbar);
            Assert.True(window.Topmost);
            Assert.Equal(WindowStyle.None, window.WindowStyle);
        });
    }

    [Fact]
    public void FlyoutShowsTheFooterAndBannerWhenAnApplyFailed()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        File.WriteAllText(h.ClaudeConfigPath, "{oops");
        state.SetEnabled("aws-mcp", false);
        var services = new AppServices(h.Settings, new FakeClaudeInstall(), h.Claude, h.Notifier, new FakeAutostart(), new FakeUpdater());
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            using var model = new FlyoutModel(state);
            var window = new FlyoutWindow(model, new WindowRegistry(state, services, updates));
            Layout(window);
            Assert.Equal(Visibility.Visible, window.FooterPanel.Visibility);
            Assert.Equal("Apply Failed — Retry", window.FooterTitle.Text);
            Assert.Equal(Visibility.Visible, window.ErrorBanner.Visibility);
        });
    }

    [Fact]
    public void TheProfileMenuKeepsTheFlyoutOpenButAPlainDeactivationHidesIt()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var services = new AppServices(h.Settings, new FakeClaudeInstall(), h.Claude, h.Notifier, new FakeAutostart(), new FakeUpdater());
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            using var model = new FlyoutModel(state);
            var registry = new WindowRegistry(state, services, updates);

            var plain = new FlyoutWindow(model, registry) { TrayAnchor = () => null };
            plain.Show();
            plain.HandleDeactivated();                       // clicking away dismisses (catalog §2.1)
            Assert.False(plain.IsVisible);

            var withMenu = new FlyoutWindow(model, registry) { TrayAnchor = () => null };
            withMenu.Show();
            var menu = withMenu.OpenProfileMenu();
            Assert.True(withMenu.HasOpenPopup);
            withMenu.HandleDeactivated();                    // the menu's own window took the focus
            Assert.True(withMenu.IsVisible);                 // …which is not a dismissal
            Assert.Equal(DateTime.MinValue, withMenu.LastHiddenUtc);
            withMenu.HideFlyout();
            menu.IsOpen = false;   // leave nothing behind in the shared WPF host
            withMenu.Close();
            plain.Close();
        });
    }

    [Fact]
    public void TrayMenuHasOpenSettingsAndQuit()
    {
        var clicks = new List<string>();
        var headers = WpfApp.Invoke(() =>
        {
            var menu = TrayController.BuildMenu(() => clicks.Add("open"), () => clicks.Add("settings"), () => clicks.Add("quit"));
            var items = menu.Items.OfType<MenuItem>().ToList();
            foreach (var item in items)
            {
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            Assert.Equal(4, menu.Items.Count);   // three items and a separator
            return items.Select(i => (string)i.Header).ToArray();
        });
        Assert.Equal(["Open", "Settings…", "Quit Connector Control"], headers);
        Assert.Equal(["open", "settings", "quit"], clicks);
    }
}

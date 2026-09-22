using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ConnectorControl.App.Services;
using ConnectorControl.App.Tests.TestSupport;
using ConnectorControl.App.Tray;
using ConnectorControl.App.Views;
using ConnectorControl.Core;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;
// Named rather than imported: System.Windows.Shapes.Path would collide with System.IO.Path,
// which this file uses to build the sample document's path.
using Ellipse = System.Windows.Shapes.Ellipse;

namespace ConnectorControl.App.Tests;

public class FlyoutWindowTests
{
    private static void Layout(Window window) => WindowTestSupport.Layout(window, new Size(380, 800));

    /// <summary>
    /// The pickers the flyout would have put in front of itself and the refusal it would have
    /// shown. Every test drives the window on the one dispatcher it lives on, where a real modal
    /// would block the test that opened it and a real picker would wait for a person.
    /// </summary>
    private sealed class Recorder
    {
        /// <summary>What the file picker answers; null is the user cancelling it.</summary>
        public string? Document { get; set; }

        public string? Folder { get; set; }

        public List<string> Informed { get; } = [];

        public FlyoutWindow.Presenters Presenters => new(() => Document, () => Folder, Informed.Add);
    }

    /// <summary>
    /// One flyout with its pickers recorded instead of shown, hidden however the body ends, and
    /// never both visible and active while it lives. Closing it only hides it — OnClosing cancels
    /// while the app is alive — so the helper does both. <paramref name="rows"/> buys the real
    /// layout pass an ItemsControl needs before it generates any container.
    /// </summary>
    private static void Showing(AppStateHarness h, AppState state,
        Action<FlyoutWindow, FlyoutModel, Recorder> body, bool rows = false)
    {
        var services = h.Services();
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        using var model = new FlyoutModel(state, h.Settings);
        var registry = new WindowRegistry(state, services, updates, h.Dialogs);
        var window = new FlyoutWindow(model, registry) { TrayAnchor = () => null };
        var recorder = new Recorder();
        window.Surfaces = recorder.Presenters;
        try
        {
            // Bindings settle first, while nothing of ours is on screen, because that is the step
            // that pumps. Between Show and Hide there is no pump at all: UpdateLayout runs the
            // real layout pass — the one that generates the rows — synchronously.
            Layout(window);
            if (rows)
            {
                window.Show();
                window.UpdateLayout();
                window.HideFlyout();
                Layout(window);
            }
            body(window, model, recorder);
        }
        finally
        {
            window.HideFlyout();
            window.Close();
        }
    }

    /// <summary>
    /// Subscribes the harness's state to the sample document on disk, so "Data team" is a real
    /// synced collection beside the harness's local one, and leaves Default active.
    /// </summary>
    private static void SubscribeToDataTeam(AppStateHarness h, AppState state)
    {
        File.WriteAllBytes(DataTeamPath(h), CollectionDocumentSamples.DataTeam.Serialize());
        Assert.Null(state.Subscribe(DataTeamPath(h), null));
        state.SwitchCollection("Default");
    }

    private static string DataTeamPath(AppStateHarness h)
    {
        var path = h.Dir.File(Path.Combine("shared", "data-team.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>The sample with one connector gone: a change at the source, which is all a banner needs.</summary>
    private static CollectionDocument Without(string removed)
    {
        var sample = CollectionDocumentSamples.DataTeam;
        var connectors = new Dictionary<string, CollectionDocument.Connector>(sample.Connectors, StringComparer.Ordinal);
        connectors.Remove(removed);
        return new CollectionDocument(sample.Name, sample.Author, sample.Origin, sample.Exported, connectors);
    }

    /// <summary>
    /// Publishes the active collection, then puts a file where its folder was: that write fails
    /// on both platforms and needs no permission games, and the next store change raises the
    /// banner. Returns the path that is now a file rather than a folder.
    /// </summary>
    private static string BlockedPublish(AppStateHarness h, AppState state)
    {
        var folder = h.Dir.File("pub");
        Directory.CreateDirectory(folder);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        Directory.Delete(folder, recursive: true);
        File.WriteAllText(folder, "not a folder");
        Assert.Null(state.Upsert("blocked", new McpEntry(AppStateHarness.Remote("https://example.test/")), null));
        Assert.NotNull(state.PublishError);
        return folder;
    }

    private static void Click(ButtonBase button) =>
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    /// <summary>A collection item's name: the first TextBlock of its header, ahead of its marks.</summary>
    private static string MenuName(MenuItem item) =>
        ((StackPanel)item.Header).Children.OfType<TextBlock>().First().Text;

    /// <summary>The chain after a collection's name in the menu, or null for a local collection.</summary>
    private static TextBlock? MenuChain(MenuItem item) =>
        ((StackPanel)item.Header).Children.OfType<TextBlock>().Skip(1).SingleOrDefault();

    /// <summary>The amber dot after a collection's name, or null when nothing is waiting.</summary>
    private static Ellipse? MenuDot(MenuItem item) =>
        ((StackPanel)item.Header).Children.OfType<Ellipse>().SingleOrDefault();

    /// <summary>The menu's commands: everything after the one item per collection.</summary>
    private static string[] MenuCommands(ContextMenu menu, FlyoutModel model) =>
        menu.Items.OfType<MenuItem>().Skip(model.CollectionItems.Count).Select(i => (string)i.Header).ToArray();

    private static TextBlock RowLock(FlyoutWindow window, ConnectorRow row) =>
        RowElements.Find<TextBlock>(window.RowList, row, "RowLockGlyph");

    [Fact]
    public void FlyoutShowsHeaderRowsAndNoFooterWhenNothingIsPending()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var services = h.Services();
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            using var model = new FlyoutModel(state, h.Settings);
            var window = new FlyoutWindow(model, new WindowRegistry(state, services, updates));
            Layout(window);
            Assert.Equal(3, window.RowList.Items.Count);
            Assert.Equal(Visibility.Collapsed, window.FooterPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, window.ErrorBanner.Visibility);
            Assert.Equal(Visibility.Collapsed, window.EmptyLabel.Visibility);
            Assert.Equal("3 of 3 enabled", window.SubtitleText.Text);
            Assert.Equal("Default", window.CollectionChipName.Text);
            Assert.Equal(Visibility.Collapsed, window.CollectionBannerStrip.Visibility);
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
        var services = h.Services();
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            using var model = new FlyoutModel(state, h.Settings);
            var window = new FlyoutWindow(model, new WindowRegistry(state, services, updates));
            Layout(window);
            Assert.Equal(Visibility.Visible, window.FooterPanel.Visibility);
            Assert.Equal("Apply Failed — Retry", window.FooterTitle.Text);
            Assert.Equal(Visibility.Visible, window.ErrorBanner.Visibility);
        });
    }

    [Fact]
    public void TheCollectionMenuKeepsTheFlyoutOpenButAPlainDeactivationHidesIt()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var services = h.Services();
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            using var model = new FlyoutModel(state, h.Settings);
            var registry = new WindowRegistry(state, services, updates);

            var plain = new FlyoutWindow(model, registry) { TrayAnchor = () => null };
            plain.Show();
            plain.HandleDeactivated();                       // clicking away dismisses
            Assert.False(plain.IsVisible);

            var withMenu = new FlyoutWindow(model, registry) { TrayAnchor = () => null };
            withMenu.Show();
            var menu = withMenu.OpenCollectionMenu();
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

    /// <summary>
    /// The collection chip menu puts a check mark on the active collection. The Fluent MenuItem template only
    /// gives an item a check column when it is checkable, so IsChecked alone drew nothing.
    /// </summary>
    [Fact]
    public void TheCollectionMenuChecksTheActiveCollectionAndNothingElse()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Work"));   // Default + Work, with Work active
        var services = h.Services();
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            using var model = new FlyoutModel(state, h.Settings);
            var window = new FlyoutWindow(model, new WindowRegistry(state, services, updates)) { TrayAnchor = () => null };
            window.Show();
            var menu = window.OpenCollectionMenu();

            var collections = menu.Items.OfType<MenuItem>().Take(model.CollectionItems.Count).ToList();
            Assert.Equal(["Default", "Work"], collections.Select(MenuName).ToArray());
            foreach (var (item, expected) in collections.Zip(model.CollectionItems))
            {
                Assert.True(item.IsCheckable);
                Assert.Equal(expected.IsActive, item.IsChecked);
            }
            Assert.Equal(["Work"], collections.Where(i => i.IsChecked).Select(MenuName).ToArray());

            menu.IsOpen = false;   // leave nothing behind in the shared WPF host
            window.HideFlyout();
            window.Close();
        });
    }

    [Fact]
    public void TheChipShowsTheChainOnlyForASyncedCollection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        SubscribeToDataTeam(h, state);
        WpfApp.Invoke(() => Showing(h, state, (window, model, _) =>
        {
            Assert.Equal("Default", window.CollectionChipName.Text);
            Assert.Equal(Visibility.Collapsed, window.CollectionChipChain.Visibility);
            Assert.Equal(Visibility.Collapsed, window.CollectionChipDot.Visibility);

            state.SwitchCollection("Data team");
            Layout(window);
            Assert.Equal("Data team", window.CollectionChipName.Text);
            Assert.Equal(Visibility.Visible, window.CollectionChipChain.Visibility);
            // The chain says where the document is, which is the one thing the chip cannot show.
            // The path is the one the state recorded, not the one this test wrote: a temp
            // directory can reach the same file under more than one spelling.
            var source = state.SourceLocation("Data team");
            Assert.NotNull(source);
            Assert.Equal(FlyoutModel.SourceTooltipFormat(source), window.CollectionChipChain.ToolTip);
            Assert.Equal(FlyoutModel.SourceTooltipFormat(source), AutomationProperties.GetName(window.CollectionChipChain));
            Assert.Equal(Visibility.Collapsed, window.CollectionChipDot.Visibility);   // nothing waiting yet

            state.SwitchCollection("Default");
            Layout(window);
            Assert.Equal(Visibility.Collapsed, window.CollectionChipChain.Visibility);
        }));
    }

    [Fact]
    public void TheMenuHasImportExportAndManageButNoHousekeeping()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        SubscribeToDataTeam(h, state);
        WpfApp.Invoke(() => Showing(h, state, (window, model, _) =>
        {
            var menu = window.BuildCollectionMenu();
            Assert.Equal(["Import…", "Export “Default”…", "Manage Collections…"], MenuCommands(menu, model));
            Assert.Equal(2, menu.Items.OfType<Separator>().Count());
            var collections = menu.Items.OfType<MenuItem>().Take(model.CollectionItems.Count).ToList();
            Assert.Equal(["Data team", "Default"], collections.Select(MenuName).ToArray());
            // Every synced row names its source, not only the active one the chip speaks for:
            // Default is active here and Data team is not.
            var chain = MenuChain(collections[0]);
            Assert.NotNull(chain);
            var source = state.SourceLocation("Data team");
            Assert.NotNull(source);
            Assert.Equal(FlyoutModel.SourceTooltipFormat(source), chain.ToolTip);
            Assert.Equal(FlyoutModel.SourceTooltipFormat(source), AutomationProperties.GetName(chain));
            Assert.Null(MenuChain(collections[1]));
            Assert.Null(MenuDot(collections[0]));   // nothing waiting
            // A header built from elements announces nothing of its own; each row is named with
            // the model's title instead.
            Assert.Equal(model.CollectionItems.Select(FlyoutModel.MenuTitle).ToArray(),
                collections.Select(i => AutomationProperties.GetName(i)).ToArray());

            // A synced collection is the author's document already; this machine does not offer
            // to pass a second copy of it on.
            state.SwitchCollection("Data team");
            Layout(window);
            Assert.Equal(["Import…", "Manage Collections…"], MenuCommands(window.BuildCollectionMenu(), model));
        }));
    }

    [Fact]
    public void ARowInASyncedCollectionShowsTheLockAndAddIsDisabled()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        SubscribeToDataTeam(h, state);
        WpfApp.Invoke(() => Showing(h, state, (window, model, _) =>
        {
            Assert.True(window.AddButton.IsEnabled);
            Assert.Equal(FlyoutModel.AddTooltip, window.AddButton.ToolTip);
            Assert.Equal(Visibility.Collapsed, RowLock(window, model.Rows[0]).Visibility);
        }, rows: true));

        state.SwitchCollection("Data team");
        WpfApp.Invoke(() => Showing(h, state, (window, model, _) =>
        {
            Assert.False(window.AddButton.IsEnabled);
            Assert.Equal(FlyoutModel.AddDisabledTooltip, window.AddButton.ToolTip);
            var row = model.Rows[0];
            var glyph = RowLock(window, row);
            Assert.Equal(Visibility.Visible, glyph.Visibility);
            Assert.Equal(row.LockTooltip, glyph.ToolTip);
            Assert.Equal(row.LockTooltip, AutomationProperties.GetName(glyph));
        }, rows: true));
    }

    [Fact]
    public void TheCollectionBannerShowsAPendingUpdate()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        SubscribeToDataTeam(h, state);
        state.SwitchCollection("Data team");
        // The document changes under the collection, read back through Refresh — the same read
        // the watcher would drive, without waiting for one.
        File.WriteAllBytes(DataTeamPath(h), Without("github").Serialize());
        state.RefreshSource("Data team");
        Assert.True(state.PendingUpdates.ContainsKey("Data team"));

        WpfApp.Invoke(() => Showing(h, state, (window, model, _) =>
        {
            Assert.Equal(Visibility.Visible, window.CollectionBannerStrip.Visibility);
            Assert.Equal(model.CollectionBannerText, window.CollectionBannerMessage.Text);
            Assert.Contains("removes github", window.CollectionBannerMessage.Text);
            Assert.Equal(FlyoutModel.ReviewAndApplyButton, window.CollectionBannerButton.Content);
            // One answer, so no second button; the chip repeats the news beside the name.
            Assert.Equal(Visibility.Collapsed, window.CollectionBannerSecondary.Visibility);
            Assert.Equal(Visibility.Visible, window.CollectionChipDot.Visibility);
            Assert.Equal(FlyoutModel.PendingSpokenLabel, AutomationProperties.GetName(window.CollectionChipDot));

            // So does the menu: a dot that speaks, and a row whose name carries the same words.
            var row = window.BuildCollectionMenu().Items.OfType<MenuItem>().First();
            Assert.Equal("Data team", MenuName(row));
            Assert.Equal("Data team" + FlyoutModel.PendingMenuMark, AutomationProperties.GetName(row));
            var dot = MenuDot(row);
            Assert.NotNull(dot);
            Assert.Equal(FlyoutModel.PendingSpokenLabel, dot.ToolTip);
            Assert.Equal(FlyoutModel.PendingSpokenLabel, AutomationProperties.GetName(dot));
        }));
    }

    [Fact]
    public void TheFailedPublishBannerOffersStopPublishingAsWellAsAnotherFolder()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        BlockedPublish(h, state);

        WpfApp.Invoke(() => Showing(h, state, (window, _, _) =>
        {
            Assert.Equal(Visibility.Visible, window.CollectionBannerStrip.Visibility);
            Assert.Equal(FlyoutModel.ChooseFolderButton, window.CollectionBannerButton.Content);
            Assert.Equal(Visibility.Visible, window.CollectionBannerSecondary.Visibility);
            Assert.Equal(CollectionsModel.StopPublishingAction, window.CollectionBannerSecondary.Content);

            // Giving up on the folder is the one banner answer that asks nothing of the user
            // first, and it takes the banner with it.
            Click(window.CollectionBannerSecondary);
            Layout(window);
            Assert.Null(state.PublishError);
            Assert.False(state.IsPublished(state.ActiveCollection));
            Assert.Equal(Visibility.Collapsed, window.CollectionBannerStrip.Visibility);
        }));
    }

    [Fact]
    public void TheFailedPublishBannerRepublishesToTheFolderYouChoose()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        BlockedPublish(h, state);
        var chosen = h.Dir.File("elsewhere");
        Directory.CreateDirectory(chosen);

        WpfApp.Invoke(() => Showing(h, state, (window, _, recorder) =>
        {
            // Cancelling the picker changes nothing and says nothing.
            Click(window.CollectionBannerButton);
            Layout(window);
            Assert.Empty(recorder.Informed);
            Assert.NotNull(state.PublishError);
            Assert.Equal(Visibility.Visible, window.CollectionBannerStrip.Visibility);

            recorder.Folder = chosen;
            Click(window.CollectionBannerButton);
            Layout(window);
            Assert.Empty(recorder.Informed);
            Assert.Null(state.PublishError);
            Assert.True(state.IsPublished(state.ActiveCollection));
            Assert.Single(Directory.GetFiles(chosen, "*.json"));   // the document followed the folder
            Assert.Equal(Visibility.Collapsed, window.CollectionBannerStrip.Visibility);
        }));
    }

    [Fact]
    public void AFolderThatRefusesTheWriteTooIsSaidSoAndTheBannerStays()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var blocked = BlockedPublish(h, state);

        WpfApp.Invoke(() => Showing(h, state, (window, _, recorder) =>
        {
            recorder.Folder = blocked;   // the same path again: still a file where a folder belongs
            Click(window.CollectionBannerButton);
            Layout(window);
            var told = Assert.Single(recorder.Informed);
            var failure = state.PublishError;
            Assert.NotNull(failure);
            Assert.Equal(failure.Message, told);
            Assert.Equal(Visibility.Visible, window.CollectionBannerStrip.Visibility);
        }));
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

    [Fact]
    public void ARowWhoseLauncherIsMissingShowsTheCautionGlyphAndAnInstalledOneDoesNot()
    {
        using var h = new AppStateHarness();
        h.Tools.Statuses[Tool.Npx] = ToolStatus.NotFound;
        h.Tools.Statuses[Tool.Node] = new ToolStatus(@"C:\Program Files\nodejs\node.exe", "22.11.0");
        using var state = h.Create();
        state.Upsert("local-node", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String("node")),
            ("args", JsonValue.Array([JsonValue.String("server.js")])))), null);
        state.RefreshToolsAsync([Tool.Npx, Tool.Node]);
        Assert.True(h.Ui.PumpUntil(() => state.ToolStatuses.Count == 2, TimeSpan.FromSeconds(5)));
        var services = h.Services();
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            using var model = new FlyoutModel(state, h.Settings);
            var window = new FlyoutWindow(model, new WindowRegistry(state, services, updates)) { TrayAnchor = () => null };
            window.Show();   // an ItemsControl generates no containers until the window has a real layout pass
            Layout(window);
            window.RowList.UpdateLayout();

            var missing = model.Rows.Single(r => r.Name == "aws-mcp");        // npx, not found
            var installed = model.Rows.Single(r => r.Name == "local-node");   // node, found
            Assert.Equal(Visibility.Visible, Glyph(window, missing).Visibility);
            Assert.Equal("Needs npx, which wasn’t found. Edit to see how to install it.", (string?)Glyph(window, missing).ToolTip);
            Assert.Equal(Visibility.Collapsed, Glyph(window, installed).Visibility);
            Assert.True(missing.Enabled);   // the glyph changes nothing about the switch

            window.HideFlyout();
            window.Close();
        });
    }

    /// <summary>The row's caution glyph: the one TextBlock in its container carrying the Warning code point.</summary>
    private static TextBlock Glyph(FlyoutWindow window, ConnectorRow row)
    {
        var container = window.RowList.ItemContainerGenerator.ContainerFromItem(row);
        Assert.NotNull(container);
        var blocks = new List<TextBlock>();
        Collect(container, blocks);
        return Assert.Single(blocks.Where(t => t.Text == FlyoutModel.ToolWarningGlyph).ToArray());
    }

    private static void Collect(DependencyObject root, List<TextBlock> found)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text)
            {
                found.Add(text);
            }
            Collect(child, found);
        }
    }
}

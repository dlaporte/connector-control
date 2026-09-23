using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ConnectorControl.App.Services;
using ConnectorControl.Core;
using ConnectorControl.App.Tests.TestSupport;
using ConnectorControl.App.Views;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;
// Named rather than imported: System.Windows.Shapes.Path would collide with System.IO.Path,
// which this file uses to build the sample document's path.
using Ellipse = System.Windows.Shapes.Ellipse;

namespace ConnectorControl.App.Tests;

public class CollectionsWindowTests
{
    private static void Layout(Window window) => WindowTestSupport.Layout(window, new Size(720, 480));

    /// <summary>
    /// Runs what the window queued below layout. It defers consuming a request — a picker must
    /// not open inside Show() — and <see cref="WindowTestSupport.Layout"/> pumps only down to
    /// DataBind, which leaves a Background item sitting there.
    /// </summary>
    private static void Pump(Window window) => window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

    /// <summary>
    /// The pickers and dialogs the window would have put in front of itself, and what it would
    /// have shown them. Every test drives the window on the one dispatcher it lives on, where a
    /// real modal would block the test that opened it and a real picker would wait for a person.
    /// </summary>
    private sealed class Recorder
    {
        /// <summary>What the file picker answers; null is the user cancelling it.</summary>
        public string? Document { get; set; }

        public string? Folder { get; set; }

        /// <summary>Which picker the window reached for, which is the banner's whole contract.</summary>
        public int DocumentAsks { get; private set; }

        public int FolderAsks { get; private set; }

        public List<ImportModel> Imports { get; } = [];

        public List<ReviewModel> Reviews { get; } = [];

        public List<(PublishModel Model, PublishDialogMode Mode)> Publishes { get; } = [];

        public List<CopyModel> Copies { get; } = [];

        public CollectionsWindow.Presenters Presenters => new(
            _ => { DocumentAsks++; return Document; },
            _ => { FolderAsks++; return Folder; },
            (_, model) => { Imports.Add(model); return true; },
            (_, model) => { Reviews.Add(model); return true; },
            (_, model, mode) => { Publishes.Add((model, mode)); return true; },
            (_, model, _) => { Copies.Add(model); return true; });
    }

    /// <summary>
    /// One Collections window with every picker and dialog recorded instead of shown, closed
    /// however the body ends, and never both visible and active while it lives — the reason
    /// <see cref="EditorWindowTests"/>'s own helper shows and hides rather than staying up.
    /// <paramref name="select"/> is applied before the layout pass that generates the rows, so
    /// the body reads the collection it asked for rather than the active one.
    ///
    /// The window and everything that drives it live inside <c>WpfApp.Invoke</c>: a Window
    /// constructed anywhere but the one STA thread WpfApp owns throws before it has a chance to
    /// be wrong about anything else. The body runs there too, because it reads that window's
    /// bindings, ticks its rows and raises the request its handler answers on that thread.
    /// </summary>
    private static void Showing(AppStateHarness h, AppState state, Action<CollectionsWindow, Recorder> body,
        string? select = null)
    {
        var services = h.Services();
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            var registry = new WindowRegistry(state, services, updates, h.Dialogs);
            var window = new CollectionsWindow(state, registry, h.Dialogs) { ShowActivated = false };
            var recorder = new Recorder();
            window.Surfaces = recorder.Presenters;
            var closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                if (select is not null)
                {
                    window.Model.Selected = select;
                }
                // Bindings settle first, while nothing of ours is on screen, because that is the
                // step that pumps. Between Show and Hide there is no pump at all: UpdateLayout
                // runs the real layout pass — the one that generates the rows — synchronously.
                Layout(window);
                window.Show();
                window.UpdateLayout();
                window.Hide();
                Layout(window);
                // The consume Loaded queued runs here, on a window with no request waiting, so
                // the body's own request is answered by the change notification and not by it.
                Pump(window);
                body(window, recorder);
            }
            finally
            {
                if (!closed)
                {
                    window.Close();
                }
            }
        });
    }

    /// <summary>
    /// Subscribes the harness's state to the sample document on disk, so "Data team" is a real
    /// synced collection beside the harness's local one, and leaves Default active. Answers with
    /// the document's path, which is what the chain glyph names.
    /// </summary>
    private static string SubscribeToDataTeam(AppStateHarness h, AppState state)
    {
        var path = Path.GetFullPath(h.Dir.File(Path.Combine("shared", "data-team.json")));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, CollectionDocumentSamples.DataTeam.Serialize());
        Assert.Null(state.Subscribe(path, null));
        state.SwitchCollection("Default");
        return path;
    }

    private static T InSidebar<T>(CollectionsWindow window, string collection, string name)
        where T : FrameworkElement =>
        RowElements.Find<T>(window.Sidebar, window.Model.Items.Single(i => i.Name == collection), name);

    /// <summary>
    /// What the Make Active item carries when the sidebar's context menu is raised over one row.
    /// The menu is opened for real, because the parameter comes from its PlacementTarget, which
    /// WPF sets only as it opens — and it is closed again, so nothing is left behind in the
    /// shared WPF host.
    /// </summary>
    private static object? MenuParameterFor(CollectionsWindow window, CollectionsModel.Item item)
    {
        var container = (ListBoxItem)window.Sidebar.ItemContainerGenerator.ContainerFromItem(item)!;
        var menu = container.ContextMenu!;
        menu.PlacementTarget = container;
        // A popup places itself against a target that is on screen, so the window goes back up
        // for exactly as long as the menu is open. Nothing between these two lines pumps the
        // dispatcher, so the window is never both visible and active while another test class's
        // queued body could run inside this one.
        window.Show();
        menu.IsOpen = true;
        try
        {
            menu.UpdateLayout();
            var entry = (MenuItem)menu.Items[0];
            Assert.Equal(CollectionsModel.MakeActiveAction, entry.Header);
            return entry.CommandParameter;
        }
        finally
        {
            menu.IsOpen = false;
            window.Hide();
        }
    }

    private static T InRow<T>(CollectionsWindow window, string connector, string name)
        where T : FrameworkElement =>
        RowElements.Find<T>(window.RowList, window.Model.Rows.Single(r => r.Name == connector), name);

    /// <summary>The separator's stand-in in <see cref="Entries"/>.</summary>
    private const string Rule = "—";

    /// <summary>
    /// A code-built menu read the way a screen reader hears it: a plain entry by its header, a
    /// two-line one by the name it is given, a separator as <see cref="Rule"/>.
    /// </summary>
    private static List<string> Entries(ContextMenu menu) =>
        menu.Items.Cast<object>().Select(entry => entry switch
        {
            Separator => Rule,
            MenuItem { Header: string header } => header,
            MenuItem item => AutomationProperties.GetName(item),
            _ => throw new InvalidOperationException(entry.GetType().Name),
        }).ToList();

    private static MenuItem Entry(ContextMenu menu, string title) =>
        menu.Items.OfType<MenuItem>().Single(item =>
            item.Header as string == title || AutomationProperties.GetName(item) == title);

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    /// <summary>Ticks or unticks one row the way a click does, then gives the rebuilt rows their pass.</summary>
    private static void Tick(CollectionsWindow window, string connector, bool on)
    {
        var tick = InRow<CheckBox>(window, connector, "RowTick");
        tick.IsChecked = on;
        tick.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, tick));
        Layout(window);
    }

    [Fact]
    public void TheSidebarListsCollectionsWithTheActiveOneBold()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var document = SubscribeToDataTeam(h, state);
        Showing(h, state, (window, _) =>
        {
            Assert.Equal(["Data team", "Default"], window.Model.Items.Select(i => i.Name));
            Assert.Equal(2, window.Sidebar.Items.Count);
            Assert.Equal("Default", window.Sidebar.SelectedValue);

            // The active collection, and only it, is bold.
            Assert.Equal(FontWeights.Bold, InSidebar<TextBlock>(window, "Default", "SidebarNameText").FontWeight);
            Assert.Equal(FontWeights.Normal, InSidebar<TextBlock>(window, "Data team", "SidebarNameText").FontWeight);

            // The chain marks the synced one and names the document behind it; nothing is
            // waiting to be reviewed.
            var chain = InSidebar<TextBlock>(window, "Data team", "SidebarChainGlyph");
            Assert.Equal(Visibility.Visible, chain.Visibility);
            var team = window.Model.Items.Single(i => i.Name == "Data team");
            Assert.Equal(document, team.Source);
            Assert.Equal(CollectionsModel.SyncedGlyphTooltip(team), chain.ToolTip);
            Assert.Equal(Visibility.Collapsed, InSidebar<TextBlock>(window, "Default", "SidebarChainGlyph").Visibility);
            Assert.Equal(Visibility.Collapsed, InSidebar<Ellipse>(window, "Data team", "SidebarPendingDot").Visibility);

            // The header names the collection; the detail line is the model's, in the idle bar.
            Assert.Equal("Default", window.SelectedNameText.Text);
            Assert.Equal(window.Model.DetailLine, window.DetailText.Text);
            Assert.Equal(Visibility.Collapsed, window.BannerStrip.Visibility);
        });
    }

    /// <summary>
    /// The loop that overflowed the test process's stack: the sidebar's selection, bound two way,
    /// and the model's Selected setter feeding each other through the Items list every raise
    /// handed the sidebar. What that loop multiplied was raises of <c>Selected</c> with nothing
    /// behind them and replacements of <c>Items</c>, so those are what is counted — by name, so the
    /// bounds do not move with how many other properties one change announces. Bounded counts
    /// rather than a timeout: the unguarded loop never returned, so without the guard this test
    /// does not fail, it takes the process down.
    /// </summary>
    [Fact]
    public void TheSidebarSelectionAndARebuildDoNotFeedEachOther()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        SubscribeToDataTeam(h, state);
        Showing(h, state, (window, _) =>
        {
            var selectedRaises = 0;
            var itemsRaises = 0;
            var stateRaises = 0;
            var selections = 0;
            window.Model.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CollectionsModel.Selected))
                {
                    selectedRaises++;
                }
                if (e.PropertyName == nameof(CollectionsModel.Items))
                {
                    itemsRaises++;
                }
            };
            state.PropertyChanged += (_, _) => stateRaises++;
            window.Sidebar.SelectionChanged += (_, _) => selections++;

            // A user picking a row reaches the model — the markup binds one way now, so this is
            // the handler's doing — and the raise it causes stops there. Picked by index, the way
            // a click picks: assigning SelectedValue would replace the very binding under test.
            // Exactly one Selected raise, and the collections list is left exactly as it was.
            window.Sidebar.SelectedIndex = window.Model.Items.ToList().FindIndex(i => i.Name == "Data team");
            Layout(window);
            Assert.Equal("Data team", window.Model.Selected);
            Assert.Equal("Data team", window.Sidebar.SelectedValue);
            Assert.Equal(1, selectedRaises);
            Assert.Equal(0, itemsRaises);
            Assert.InRange(selections, 1, 4);

            // Making that collection active rewrites every item, because IsActive moves: the
            // rebuild the two-way binding used to answer with a write of its own, and so on.
            // Items is replaced once, for that one change. Every Selected raise answers a change
            // AppState announced — the model re-announces the collection on show after each —
            // and the sidebar adds none of its own: however many notifications one switch makes,
            // a loop is a Selected raise with no state change behind it.
            selectedRaises = 0;
            itemsRaises = 0;
            stateRaises = 0;
            selections = 0;
            CollectionsWindow.MakeActiveCommand.Execute(window.Model.Items.Single(i => i.Name == "Data team"), window);
            Layout(window);
            Assert.Equal("Data team", state.ActiveCollection);
            Assert.Equal("Data team", window.Model.Selected);
            // …and the sidebar still highlights it, although every item it held was replaced.
            Assert.Equal("Data team", window.Sidebar.SelectedValue);
            Assert.Equal(1, itemsRaises);
            Assert.InRange(selectedRaises, 1, stateRaises);
            Assert.InRange(selections, 0, 6);
        });
    }

    /// <summary>
    /// The command's own two halves, with the collection handed over directly: CanExecute greys
    /// Make Active out for the collection that is already active, and the handler activates
    /// whichever collection it is given. What the real menu hands it is
    /// <see cref="TheSidebarMenuCarriesTheCollectionItWasRaisedOver"/>'s question.
    /// </summary>
    [Fact]
    public void MakeActiveIsGreyedForTheActiveCollectionAndActivatesTheOneItIsGiven()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        SubscribeToDataTeam(h, state);
        Showing(h, state, (window, _) =>
        {
            Assert.Equal("Default", state.ActiveCollection);
            var synced = window.Model.Items[0];
            var active = window.Model.Items[1];
            Assert.Equal("Data team", synced.Name);
            Assert.Equal("Default", active.Name);

            // The collection that is already active greys the item out rather than letting it do
            // nothing visible.
            Assert.False(CollectionsWindow.MakeActiveCommand.CanExecute(active, window));
            Assert.True(CollectionsWindow.MakeActiveCommand.CanExecute(synced, window));

            CollectionsWindow.MakeActiveCommand.Execute(synced, window);
            Assert.Equal("Data team", state.ActiveCollection);

            // Then back to the other one: the handler follows its parameter both ways.
            var second = window.Model.Items[1];
            Assert.Equal("Default", second.Name);
            Assert.True(CollectionsWindow.MakeActiveCommand.CanExecute(second, window));
            CollectionsWindow.MakeActiveCommand.Execute(second, window);
            Assert.Equal("Default", state.ActiveCollection);
        });
    }

    [Fact]
    public void TheSidebarMenuCarriesTheCollectionItWasRaisedOver()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        SubscribeToDataTeam(h, state);
        Assert.Null(state.CreateCollection("Spare"));
        state.SwitchCollection("Default");
        Showing(h, state, (window, _) =>
        {
            Assert.Equal(["Data team", "Default", "Spare"], window.Model.Items.Select(i => i.Name));
            var synced = window.Model.Items[0];
            var spare = window.Model.Items[2];

            // One ContextMenu instance is shared by every container the item container style
            // makes, and WPF drops its inheritance context as soon as a second container claims
            // it. The parameter follows the PlacementTarget instead, which is the one thing
            // about that shared menu still true per row: the first row claims it…
            Assert.Equal(synced, MenuParameterFor(window, synced));
            // …and the third takes it over.
            var parameter = MenuParameterFor(window, spare);
            Assert.Equal(spare, parameter);

            CollectionsWindow.MakeActiveCommand.Execute(parameter, window);
            Assert.Equal("Spare", state.ActiveCollection);
        });
    }

    [Fact]
    public void SelectingASyncedCollectionLocksRowsAndDisablesExportAndPublish()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        SubscribeToDataTeam(h, state);
        Showing(h, state, (window, _) =>
        {
            Assert.Equal("Data team", window.Model.Selected);
            Assert.NotEmpty(window.Model.Rows);
            foreach (var row in window.Model.Rows)
            {
                var lockGlyph = InRow<TextBlock>(window, row.Name, "RowLockGlyph");
                Assert.Equal(Visibility.Visible, lockGlyph.Visibility);
                // The glyph is a private-use code point that reads as nothing, so the model's
                // sentence is both what the pointer uncovers and what a screen reader says.
                Assert.Equal(CollectionsModel.LockedGlyphTooltip, lockGlyph.ToolTip);
                Assert.Equal(CollectionsModel.LockedGlyphTooltip, AutomationProperties.GetName(lockGlyph));
                // A synced collection's rows are the author's, so they cannot be ticked either —
                // and with nothing to tick, the selection bar never offers Export or Remove.
                Assert.Equal(Visibility.Collapsed, InRow<CheckBox>(window, row.Name, "RowTick").Visibility);
                Assert.Equal(row.Target, InRow<TextBlock>(window, row.Name, "RowTargetText").Text);
            }
            Assert.Equal(Visibility.Collapsed, window.TickedBar.Visibility);

            // The Subscribed pill, and not the Active one: Default is still the active collection.
            Assert.Equal([CollectionsModel.Pill.Subscribed], window.PillList.Items.Cast<CollectionsModel.Pill>());

            // The More menu offers what a subscription can do, and no publishing. Export All is
            // listed but dimmed; the collection is not the last one, so it can be deleted.
            var menu = window.BuildCollectionMenu();
            Assert.Equal(
                [
                    CollectionsModel.MakeActiveAction, Rule,
                    CollectionsModel.RenameAction, CollectionsModel.MakeLocalCopyButton, Rule,
                    CollectionsModel.RefreshButton, CollectionsModel.ShowSourceFileAction,
                    CollectionsModel.StopSyncingAction, CollectionsModel.ExportAllAction, Rule,
                    CollectionsModel.DeleteAction,
                ],
                Entries(menu));
            Assert.False(Entry(menu, CollectionsModel.ExportAllAction).IsEnabled);
            Assert.True(Entry(menu, CollectionsModel.DeleteAction).IsEnabled);
            Assert.True(Entry(menu, CollectionsModel.StopSyncingAction).IsEnabled);

            // Make Active acts on the collection on show.
            Click(Entry(menu, CollectionsModel.MakeActiveAction));
            Assert.Equal("Data team", state.ActiveCollection);
        }, select: "Data team");
    }

    [Fact]
    public void TheCollectionMenuFollowsTheModelFlags()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        SubscribeToDataTeam(h, state);
        Showing(h, state, (window, recorder) =>
        {
            // The active local collection: no Make Active, publishing not started yet, and the
            // last local collection stays, so Delete is refused before it is offered.
            Assert.Equal("Default", window.Model.Selected);
            Assert.Equal([CollectionsModel.Pill.Active], window.PillList.Items.Cast<CollectionsModel.Pill>());
            var menu = window.BuildCollectionMenu();
            Assert.Equal(
                [
                    CollectionsModel.RenameAction, CollectionsModel.DuplicateAction, Rule,
                    CollectionsModel.PublishButton, CollectionsModel.ExportAllAction, Rule,
                    CollectionsModel.DeleteAction,
                ],
                Entries(menu));
            Assert.False(Entry(menu, CollectionsModel.DeleteAction).IsEnabled);
            Assert.True(Entry(menu, CollectionsModel.ExportAllAction).IsEnabled);
            Assert.Equal(CollectionsModel.MoreActionsLabel, window.MoreButton.ToolTip);
            Assert.Equal(CollectionsModel.MoreActionsLabel, AutomationProperties.GetName(window.MoreButton));

            // Export All offers the whole collection, whatever is ticked; Start Publishing opens
            // the Publish dialog over it.
            window.Model.SetChecked(window.Model.Rows[0].Name, true);
            Click(Entry(menu, CollectionsModel.ExportAllAction));
            Click(Entry(menu, CollectionsModel.PublishButton));
            Assert.Collection(recorder.Publishes,
                export =>
                {
                    Assert.Equal(PublishDialogMode.Export, export.Mode);
                    Assert.Equal("Default", export.Model.Collection);
                    Assert.Null(export.Model.Connectors);
                },
                publish =>
                {
                    Assert.Equal(PublishDialogMode.Publish, publish.Mode);
                    Assert.Equal("Default", publish.Model.Collection);
                    Assert.Null(publish.Model.Connectors);
                });
        });
    }

    [Fact]
    public void TheSidebarPlusSaysWhatItAdds()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Showing(h, state, (window, _) =>
        {
            Assert.Equal(CollectionsModel.AddCollectionTooltip, window.AddCollectionButton.ToolTip);
            Assert.Equal(CollectionsModel.AddCollectionTooltip, AutomationProperties.GetName(window.AddCollectionButton));
        });
    }

    [Fact]
    public void TheSidebarPlusMakesACollectionOrBringsOneIn()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var document = SubscribeToDataTeam(h, state);
        Showing(h, state, (window, recorder) =>
        {
            var menu = window.BuildSidebarMenu();
            Assert.Equal(
                [CollectionsModel.NewButton, Rule, CollectionsModel.ImportButton, CollectionsModel.SubscribeButton],
                Entries(menu));
            // A composed header announces nothing of its own, so the subtitle is the help text.
            Assert.Equal(CollectionsModel.ImportSubtitle, AutomationProperties.GetHelpText(Entry(menu, CollectionsModel.ImportButton)));
            Assert.Equal(CollectionsModel.SubscribeSubtitle, AutomationProperties.GetHelpText(Entry(menu, CollectionsModel.SubscribeButton)));

            // Import and Subscribe are the same picker, and the dialog opens in the mode asked for.
            recorder.Document = document;
            Click(Entry(menu, CollectionsModel.ImportButton));
            Click(Entry(menu, CollectionsModel.SubscribeButton));
            Assert.Equal(2, recorder.DocumentAsks);
            Assert.Collection(recorder.Imports,
                import => Assert.Equal(ImportModel.Mode.AddToCollection, import.ImportMode),
                subscribe => Assert.Equal(ImportModel.Mode.KeepInSync, subscribe.ImportMode));
        });
    }

    [Fact]
    public void TheListHeadersAddIsDisabledForASyncedCollection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        SubscribeToDataTeam(h, state);
        Showing(h, state, (window, _) =>
        {
            Assert.Equal("Default", window.Model.Selected);
            Assert.True(window.AddConnectorButton.IsEnabled);
            Assert.Equal(CollectionsModel.AddConnectorTooltip, window.AddConnectorButton.ToolTip);
            Assert.Equal(CollectionsModel.AddConnectorTooltip, AutomationProperties.GetName(window.AddConnectorButton));
            Assert.Equal(window.Model.Rows.Count.ToString(System.Globalization.CultureInfo.CurrentCulture),
                window.ConnectorCountText.Text);

            // The same button on a synced collection is dead, and its tooltip says where
            // additions go instead.
            window.Model.Selected = "Data team";
            Layout(window);
            Assert.False(window.AddConnectorButton.IsEnabled);
            Assert.Equal(CollectionsModel.AddConnectorDisabledTooltip, window.AddConnectorButton.ToolTip);
            Assert.Equal(CollectionsModel.AddConnectorDisabledTooltip, AutomationProperties.GetName(window.AddConnectorButton));
            Assert.Equal(window.Model.Rows.Count.ToString(System.Globalization.CultureInfo.CurrentCulture),
                window.ConnectorCountText.Text);
        });
    }

    [Fact]
    public void TheSelectionBarShowsTheDetailLineWhenNothingIsTicked()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Showing(h, state, (window, _) =>
        {
            Assert.Empty(window.Model.CheckedNames);
            Assert.Equal(Visibility.Visible, window.DetailText.Visibility);
            Assert.Equal(window.Model.DetailLine, window.DetailText.Text);
            Assert.Equal(Visibility.Collapsed, window.TickedBar.Visibility);

            // A tick swaps the line for the actions, and the last untick swaps it back.
            var first = window.Model.Rows[0].Name;
            Tick(window, first, true);
            Assert.Equal(Visibility.Collapsed, window.DetailText.Visibility);
            Assert.Equal(Visibility.Visible, window.TickedBar.Visibility);
            Tick(window, first, false);
            Assert.Equal(Visibility.Visible, window.DetailText.Visibility);
            Assert.Equal(Visibility.Collapsed, window.TickedBar.Visibility);
        });
    }

    [Fact]
    public void TickingRowsFillsTheSelectionBar()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Showing(h, state, (window, recorder) =>
        {
            var first = window.Model.Rows[0].Name;
            var second = window.Model.Rows[1].Name;
            Assert.Equal(Visibility.Visible, InRow<CheckBox>(window, first, "RowTick").Visibility);
            Assert.Equal(CollectionsModel.EditTooltip, InRow<Button>(window, first, "RowEdit").ToolTip);

            Tick(window, first, true);
            Tick(window, second, true);
            Assert.Equal([first, second], window.Model.CheckedNames);
            Assert.Equal(CollectionsModel.SelectedCount(2), window.SelectedCountText.Text);
            Assert.Equal(CollectionsModel.CopyToButton, window.CopyToButton.Content);
            // Export carries no count: the bar says it already.
            Assert.Equal(CollectionsModel.ExportCheckedButton, window.ExportCheckedButton.Content);
            Assert.Equal(CollectionsModel.RemoveCheckedButton, window.RemoveCheckedButton.Content);
            Assert.Equal(Visibility.Visible, window.RemoveCheckedButton.Visibility);

            // What the bar's Export then exports is the ticked connectors, into the file name the
            // collection's slug makes — the subset changes what travels, not what it is called.
            Click(window.ExportCheckedButton);
            var (exported, mode) = Assert.Single(recorder.Publishes);
            Assert.Equal(PublishDialogMode.Export, mode);
            Assert.Equal([first, second], exported.Connectors);
            Assert.Equal(Slug.Make(window.Model.Selected!) + "." + CollectionDocument.FileExtension, exported.FileName);
            Assert.Equal(new PublishModel(state, window.Model.Selected!).FileName, exported.FileName);

            // The tick survives the rebuild the model raises, and unticking takes the count back.
            Assert.True(InRow<CheckBox>(window, first, "RowTick").IsChecked);
            Tick(window, second, false);
            Assert.Equal(CollectionsModel.SelectedCount(1), window.SelectedCountText.Text);

            // Remove asks first, and removes what is ticked.
            h.Dialogs.NextConfirm = true;
            Click(window.RemoveCheckedButton);
            Layout(window);
            Assert.Equal(CollectionsModel.RemoveCheckedMessage([first]), h.Dialogs.Confirms[0].Message);
            Assert.DoesNotContain(first, window.Model.Rows.Select(r => r.Name));
            Assert.Contains(second, window.Model.Rows.Select(r => r.Name));
            Assert.Empty(window.Model.CheckedNames);
            Assert.Equal(Visibility.Collapsed, window.TickedBar.Visibility);
        });
    }

    [Fact]
    public void TheCopyToMenuListsEveryOtherCollectionWithSyncedOnesDisabled()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        SubscribeToDataTeam(h, state);
        // Spare starts as a copy of Default, so everything ticked there clashes; Empty holds
        // nothing, so a copy into it goes straight through.
        Assert.Null(state.CreateCollection("Spare"));
        Assert.Null(state.AddEmptyCollection("Empty"));
        state.SwitchCollection("Default");
        Showing(h, state, (window, recorder) =>
        {
            Assert.Equal("Default", window.Model.Selected);
            var first = window.Model.Rows[0].Name;
            Tick(window, first, true);

            // Every collection but this one, in the sidebar's order; the synced one is listed but
            // dead, and says why under its name.
            var menu = window.BuildCopyMenu();
            Assert.Equal(["Data team", "Empty", "Spare", Rule, CollectionsModel.NewButton], Entries(menu));
            var synced = Entry(menu, "Data team");
            Assert.False(synced.IsEnabled);
            Assert.Equal(CollectionsModel.ReadOnlyNote, AutomationProperties.GetHelpText(synced));
            Assert.True(Entry(menu, "Empty").IsEnabled);
            Assert.True(Entry(menu, "Spare").IsEnabled);

            // A clash asks first, about the destination chosen, and copies nothing yet.
            Click(Entry(menu, "Spare"));
            var asked = Assert.Single(recorder.Copies);
            Assert.Equal("Spare", asked.Destination);
            var row = Assert.Single(asked.Rows);
            Assert.Equal(first, row.Name);
            Assert.True(row.Clashes);
            Assert.Equal([first], window.Model.CheckedNames);

            // Nothing in the way goes straight through, and the ticks go with it.
            Click(Entry(menu, "Empty"));
            Assert.Single(recorder.Copies);
            Assert.True(state.Store.Collections["Empty"].Mcps.ContainsKey(first));
            Assert.Empty(window.Model.CheckedNames);
            Assert.Empty(h.Dialogs.Informs);
        });
    }

    [Fact]
    public void TheCopyToMenuHasNoSeparatorWithNowhereElseToGo()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Showing(h, state, (window, _) =>
        {
            Tick(window, window.Model.Rows[0].Name, true);
            Assert.Equal([CollectionsModel.NewButton], Entries(window.BuildCopyMenu()));
        });
    }

    [Fact]
    public void TheBannerAsksForTheDocumentWhenTheSourceIsNotLocated()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        // A real document to point the picker at, and a collection the sidecar calls synced with
        // nothing bound to it here — which is the Locate banner.
        var document = Path.GetFullPath(h.Dir.File(Path.Combine("shared", "data-team.json")));
        Directory.CreateDirectory(Path.GetDirectoryName(document)!);
        File.WriteAllBytes(document, CollectionDocumentSamples.DataTeam.Serialize());
        Assert.Null(state.CreateCollection("Team"));
        state.SwitchCollection("Default");
        new CollectionsFile([new KeyValuePair<string, CollectionsFile.Entry>(
            "Team", new CollectionsFile.Entry(CollectionKind.Synced, "team.json"))])
            .Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        new CollectionsLocalCache([], []).Save(state.Service.Paths.CollectionsCachePath);
        state.Reload();
        Assert.False(state.IsLocated("Team"));

        Showing(h, state, (window, recorder) =>
        {
            recorder.Document = document;
            Assert.Equal(Visibility.Visible, window.BannerStrip.Visibility);
            Assert.Equal(window.Model.BannerText, window.BannerText.Text);
            Assert.Equal(window.Model.BannerButton, window.BannerButton.Content);

            window.BannerButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, window.BannerButton));

            // This banner asks for a file, not for a folder, and what it is given is bound.
            Assert.Equal(1, recorder.DocumentAsks);
            Assert.Equal(0, recorder.FolderAsks);
            Assert.True(state.IsLocated("Team"));
            Assert.Empty(h.Dialogs.Informs);
        }, select: "Team");
    }

    /// <summary>
    /// A publish the app stopped for review, as the flyout's banner hands it over: the window,
    /// already open on another collection, moves to the one the request names and opens the
    /// Publish dialog over the whole of it — never a picker.
    /// </summary>
    [Fact]
    public void APublishRequestSelectsItsCollectionAndOpensThePublishDialog()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Spare"));
        state.SwitchCollection("Default");
        state.PublishError = new CollectionPublishError("Spare", "A marked path has moved.", PublishErrorKind.BlockedForReview);
        Showing(h, state, (window, recorder) =>
        {
            Assert.Equal("Default", window.Model.Selected);
            using var flyout = new FlyoutModel(state, h.Settings);
            Assert.True(flyout.CollectionBannerAction());
            Pump(window);

            Assert.Equal(new CollectionsWindowRequest.Publish("Spare"), window.LastRequest);
            Assert.Null(state.CollectionsWindowRequest);   // taken, so nothing acts on it twice
            Assert.Equal("Spare", window.Model.Selected);
            var (model, mode) = Assert.Single(recorder.Publishes);
            Assert.Equal(PublishDialogMode.Publish, mode);
            Assert.Equal("Spare", model.Collection);
            Assert.Null(model.Connectors);
            Assert.Equal(0, recorder.DocumentAsks);
            Assert.Equal(0, recorder.FolderAsks);
        });
    }

    /// <summary>
    /// The window's own strip over a publish blocked for review: its button opens the Publish
    /// dialog for the collection on show, and asks for no folder — another folder would re-bind
    /// the collection, write nothing there and abandon the document the old one still holds.
    /// </summary>
    [Fact]
    public void ABlockedPublishIsAnsweredInThePublishDialogNotByAFolder()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        state.PublishError = new CollectionPublishError("Default", "A marked path has moved.", PublishErrorKind.BlockedForReview);
        Showing(h, state, (window, recorder) =>
        {
            Assert.Equal(Visibility.Visible, window.BannerStrip.Visibility);
            Assert.Equal(window.Model.BannerText, window.BannerText.Text);
            Assert.Equal(window.Model.BannerButton, window.BannerButton.Content);

            window.BannerButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, window.BannerButton));

            var (model, mode) = Assert.Single(recorder.Publishes);
            Assert.Equal(PublishDialogMode.Publish, mode);
            Assert.Equal("Default", model.Collection);
            Assert.Equal(0, recorder.FolderAsks);
            Assert.Equal(0, recorder.DocumentAsks);
        });
    }

    [Fact]
    public void AWindowRequestRaisedWhileOpenIsConsumed()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        SubscribeToDataTeam(h, state);
        Showing(h, state, (window, recorder) =>
        {
            // The window is already open and showing another collection when the flyout asks for
            // the active one's Publish dialog — the case a window that only reads on load strands.
            Assert.Equal("Data team", window.Model.Selected);
            Assert.Null(window.LastRequest);
            state.CollectionsWindowRequest = new CollectionsWindowRequest.Publish(state.ActiveCollection);
            Pump(window);

            Assert.Equal(new CollectionsWindowRequest.Publish(state.ActiveCollection), window.LastRequest);
            Assert.Null(state.CollectionsWindowRequest);   // taken, so nothing acts on it twice
            Assert.Equal(state.ActiveCollection, window.Model.Selected);
            var (model, mode) = Assert.Single(recorder.Publishes);
            Assert.Equal(PublishDialogMode.Publish, mode);
            Assert.Equal(state.ActiveCollection, model.Collection);

            // A second request, after the first was consumed, reaches the window just the same.
            state.CollectionsWindowRequest = new CollectionsWindowRequest.Review("Data team");
            Pump(window);
            Assert.Equal(new CollectionsWindowRequest.Review("Data team"), window.LastRequest);
            Assert.Equal("Data team", window.Model.Selected);
            Assert.Equal("Data team", Assert.Single(recorder.Reviews).Collection);
        }, select: "Data team");
    }
}

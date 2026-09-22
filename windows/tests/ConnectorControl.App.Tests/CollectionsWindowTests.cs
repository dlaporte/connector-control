using System.Windows;
using System.Windows.Controls;
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

        public CollectionsWindow.Presenters Presenters => new(
            _ => { DocumentAsks++; return Document; },
            _ => { FolderAsks++; return Folder; },
            (_, model) => { Imports.Add(model); return true; },
            (_, model) => { Reviews.Add(model); return true; },
            (_, model, mode) => { Publishes.Add((model, mode)); return true; });
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

    private static T InRow<T>(CollectionsWindow window, string connector, string name)
        where T : FrameworkElement =>
        RowElements.Find<T>(window.RowList, window.Model.Rows.Single(r => r.Name == connector), name);

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

            // The detail line is the model's, with the name this window puts in front of it.
            Assert.Equal("Default", window.SelectedNameText.Text);
            Assert.Equal(" · " + window.Model.DetailLine, window.DetailText.Text);
            Assert.Equal(Visibility.Collapsed, window.BannerStrip.Visibility);

            // Make Active reaches the window as a command carrying the collection it was raised
            // over. The context menu holds no handler of its own: an element hooked inside a
            // lazily built subtree takes one of the window's connection ids and shifts the rest.
            Assert.True(CollectionsWindow.MakeActiveCommand.CanExecute(team, window));
            CollectionsWindow.MakeActiveCommand.Execute(team, window);
            Assert.Equal("Data team", state.ActiveCollection);
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
                // The glyph is a private-use code point, so the sentence beside it is the model's.
                Assert.Equal(CollectionsModel.LockedGlyphTooltip, lockGlyph.ToolTip);
                // A synced collection's rows cannot be exported, so they cannot be ticked either.
                Assert.Equal(Visibility.Collapsed, InRow<CheckBox>(window, row.Name, "RowTick").Visibility);
            }

            Assert.False(window.ExportButton.IsEnabled);
            Assert.False(window.PublishButton.IsEnabled);
            Assert.True(window.RefreshButton.IsEnabled);
            Assert.True(window.MakeLocalCopyButton.IsEnabled);

            Assert.Equal(Visibility.Visible, window.StopSyncingLink.Visibility);
            Assert.Equal(Visibility.Collapsed, window.PublishLink.Visibility);
            Assert.Equal(Visibility.Collapsed, window.StopPublishingLink.Visibility);
        }, select: "Data team");
    }

    [Fact]
    public void TheToolbarFollowsTheModelFlags()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        SubscribeToDataTeam(h, state);
        Showing(h, state, (window, _) =>
        {
            // A local collection with nothing ticked: Export waits for a tick, Publish is open,
            // and the two that read a source are out of reach.
            Assert.Equal("Default", window.Model.Selected);
            Assert.Equal(CollectionsModel.ExportButton(0), window.ExportButton.Content);
            Assert.False(window.ExportButton.IsEnabled);
            Assert.True(window.PublishButton.IsEnabled);
            Assert.False(window.RefreshButton.IsEnabled);
            Assert.False(window.MakeLocalCopyButton.IsEnabled);

            // The last local collection stays, so Delete is refused before it is offered.
            Assert.False(window.DeleteLink.IsEnabled);
            Assert.Equal(Visibility.Visible, window.PublishLink.Visibility);
            Assert.Equal(Visibility.Collapsed, window.StopSyncingLink.Visibility);
            Assert.Equal(Visibility.Collapsed, window.StopPublishingLink.Visibility);
        });
    }

    [Fact]
    public void CheckingRowsUpdatesTheExportCount()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Showing(h, state, (window, recorder) =>
        {
            var first = window.Model.Rows[0].Name;
            var tick = InRow<CheckBox>(window, first, "RowTick");
            Assert.Equal(Visibility.Visible, tick.Visibility);
            Assert.Equal(CollectionsModel.EditTooltip, InRow<Button>(window, first, "RowEdit").ToolTip);

            tick.IsChecked = true;
            tick.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, tick));
            // The model rebuilt the rows, so the list needs its pass before it is read into again.
            Layout(window);

            Assert.Equal([first], window.Model.CheckedNames);
            Assert.Equal(CollectionsModel.ExportButton(1), window.ExportButton.Content);
            Assert.True(window.ExportButton.IsEnabled);

            // What the button then exports is that one connector, into the file name the
            // collection's slug makes — the subset changes what travels, not what it is called.
            window.ExportButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, window.ExportButton));
            var (exported, mode) = Assert.Single(recorder.Publishes);
            Assert.Equal(PublishDialogMode.Export, mode);
            Assert.Equal([first], exported.Connectors);
            // The subset changes what travels, not what it is called: the collection's own slug,
            // which is also what a model over the whole of it would answer.
            Assert.Equal(Slug.Make(window.Model.Selected!) + "." + CollectionDocument.FileExtension, exported.FileName);
            Assert.Equal(new PublishModel(state, window.Model.Selected!).FileName, exported.FileName);

            // The tick survives the rebuild the model raises, and unticking takes the count back.
            var again = InRow<CheckBox>(window, first, "RowTick");
            Assert.True(again.IsChecked);
            again.IsChecked = false;
            again.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, again));
            Layout(window);
            Assert.Empty(window.Model.CheckedNames);
            Assert.Equal(CollectionsModel.ExportButton(0), window.ExportButton.Content);
            Assert.False(window.ExportButton.IsEnabled);
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

    [Fact]
    public void AWindowRequestRaisedWhileOpenIsConsumed()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var document = SubscribeToDataTeam(h, state);
        Showing(h, state, (window, recorder) =>
        {
            // The window is already open and showing another collection when the flyout asks for
            // the active one's Export sheet — the case a window that only reads on load strands.
            Assert.Equal("Data team", window.Model.Selected);
            Assert.Null(window.LastRequest);
            using var flyout = new FlyoutModel(state, h.Settings);
            flyout.RequestExport();
            Pump(window);

            Assert.IsType<CollectionsWindowRequest.ExportActive>(window.LastRequest);
            Assert.Null(state.CollectionsWindowRequest);   // taken, so nothing acts on it twice
            Assert.Equal(state.ActiveCollection, window.Model.Selected);
            var (model, mode) = Assert.Single(recorder.Publishes);
            Assert.Equal(PublishDialogMode.Export, mode);
            Assert.Equal(state.ActiveCollection, model.Collection);
            // The menu item offers the whole collection: a selection that has just moved carries
            // no ticks, and an empty subset would write an empty document.
            Assert.Null(model.Connectors);

            // A second request, after the first was consumed, reaches the window just the same.
            recorder.Document = document;
            flyout.RequestImport();
            Pump(window);
            Assert.IsType<CollectionsWindowRequest.ImportFile>(window.LastRequest);
            Assert.Equal(recorder.Document, Assert.Single(recorder.Imports).Path);
        }, select: "Data team");
    }
}

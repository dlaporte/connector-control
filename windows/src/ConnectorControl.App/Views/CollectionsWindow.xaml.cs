using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ConnectorControl.Core.State;
using Microsoft.Win32;

namespace ConnectorControl.App.Views;

/// <summary>
/// The Collections window: the collections in the left pane, the selected one's connectors in the
/// right, the header menu and the selection bar that act on them, and the five dialogs it puts in
/// front of itself. Layout, bindings, the native pickers, the three menus and the two formatted
/// captions; every rule and string is CollectionsModel's.
/// </summary>
public partial class CollectionsWindow : Window
{
    private readonly AppState state;
    private readonly WindowRegistry windows;
    private readonly IDialogs dialogs;
    private readonly PropertyChangedEventHandler onModelChanged;
    private readonly PropertyChangedEventHandler onStateChanged;
    /// <summary>
    /// Set the moment this window closes. What it guards is the deferred consume: a request
    /// raised just before the close runs after it, and a dead window taking one would swallow it
    /// — the next window to open would find nothing waiting.
    /// </summary>
    private bool closed;
    /// <summary>
    /// True while this window is writing the sidebar's selection into the model. The model's
    /// Selected setter raises even when nothing changed, and that raise rebuilds the sidebar's
    /// items, which can move its selection again: without this the two would take turns until
    /// the stack ran out.
    /// </summary>
    private bool writingSelection;
    /// <summary>One resync per burst of raises: a single action can raise several times.</summary>
    private bool selectionResyncQueued;

    public CollectionsWindow(AppState state, WindowRegistry windows, IDialogs dialogs)
    {
        InitializeComponent();
        this.state = state;
        this.windows = windows;
        this.dialogs = dialogs;
        Model = new CollectionsModel(state, dialogs);
        DataContext = Model;
        onModelChanged = (_, _) => Refresh();
        Model.PropertyChanged += onModelChanged;
        // WindowRegistry keeps one of these and re-activates it, so the flyout can raise a request
        // while it is already open: read on load AND on every change, or every request after the
        // first is stranded.
        onStateChanged = (_, e) =>
        {
            if (ObservableObject.Affects(e, nameof(AppState.CollectionsWindowRequest)))
            {
                ScheduleConsume();
            }
        };
        state.PropertyChanged += onStateChanged;
        // In code, not in XAML: every hookup the markup compiler numbers has to sit in this
        // window's own tree, and this one has no element to sit on.
        CommandBindings.Add(new CommandBinding(MakeActiveCommand, OnMakeActive, OnCanMakeActive));
        Loaded += (_, _) => ScheduleConsume();
        Closed += (_, _) =>
        {
            closed = true;
            state.PropertyChanged -= onStateChanged;
            Model.PropertyChanged -= onModelChanged;
            Model.Dispose();
        };
        Refresh();
    }

    /// <summary>
    /// The sidebar context menu's Make Active, as a command rather than a Click handler: a
    /// handler or an x:Name on an element inside a <c>Setter.Value</c> takes one of this
    /// window's connection ids, and WPF builds that subtree late enough that every id after it
    /// arrives at the wrong element. The menu passes the collection it was raised over.
    /// </summary>
    public static RoutedCommand MakeActiveCommand { get; } = new(nameof(MakeActiveCommand), typeof(CollectionsWindow));

    public CollectionsModel Model { get; }

    /// <summary>
    /// What this window last took from <see cref="AppState.CollectionsWindowRequest"/>. The
    /// consuming is otherwise invisible — the request is cleared as it is read — so this is how a
    /// test sees that a request raised while the window was open reached it.
    /// </summary>
    internal CollectionsWindowRequest? LastRequest { get; private set; }

    /// <summary>
    /// The two pickers and the four dialogs this window puts in front of itself. One overridable
    /// bundle, because a test drives this window on the very dispatcher it lives on: a real modal
    /// would block the test that opened it, and a real picker would wait for a person. The Copy
    /// dialog is handed the refusal a failed copy leaves in this window's model as well, which it
    /// cannot reach through CopyModel.
    /// </summary>
    internal sealed record Presenters(
        Func<Window, string?> ChooseDocument,
        Func<Window, string?> ChooseFolder,
        Func<Window, ImportModel, bool> ShowImport,
        Func<Window, ReviewModel, bool> ShowReview,
        Func<Window, PublishModel, PublishDialogMode, bool> ShowPublish,
        Func<Window, CopyModel, Func<string?>, bool> ShowCopy);

    internal static Presenters Live { get; } = new(
        PickDocument,
        PickFolder,
        (owner, model) => ImportDialog.Show(owner, model),
        (owner, model) => ReviewDialog.Show(owner, model),
        (owner, model, mode) => PublishDialog.Show(owner, model, mode),
        (owner, model, refusal) => CopyDialog.Show(owner, model, refusal));

    internal Presenters Surfaces { get; set; } = Live;

    /// <summary>
    /// The two captions built from a value rather than bound — the selection bar's count is a
    /// format, and the list header's is a number — and which half of the bar shows, which follows
    /// that same count. Everything else on this window is a binding the model raises.
    /// </summary>
    private void Refresh()
    {
        var ticked = Model.CheckedNames.Count;
        SelectedCountText.Text = CollectionsModel.SelectedCount(ticked);
        DetailText.Visibility = ticked == 0 ? Visibility.Visible : Visibility.Collapsed;
        TickedBar.Visibility = ticked == 0 ? Visibility.Collapsed : Visibility.Visible;
        ConnectorCountText.Text = Model.Rows.Count.ToString(CultureInfo.CurrentCulture);
        if (!selectionResyncQueued)
        {
            selectionResyncQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(ResyncSelection));
        }
    }

    /// <summary>
    /// Puts the model's selection back on the sidebar once a raise has been delivered to
    /// everything listening. A raise that replaces the items with records no longer equal to the
    /// selected one — making a collection active flips IsActive on all of them — clears the
    /// list's own selection, and whether the one-way binding has re-pushed by then depends on the
    /// order the notification reaches its listeners in. After it, the order no longer matters.
    /// <c>SetCurrentValue</c> keeps the binding, and the SelectionChanged it causes writes
    /// nothing, because the name it carries is already the model's.
    /// </summary>
    private void ResyncSelection()
    {
        selectionResyncQueued = false;
        if (!closed && !Equals(Sidebar.SelectedValue, Model.Selected))
        {
            Sidebar.SetCurrentValue(Selector.SelectedValueProperty, Model.Selected);
        }
    }

    // MARK: sidebar header

    private void OnAddCollection(object sender, RoutedEventArgs e) => BuildSidebarMenu().IsOpen = true;

    /// <summary>
    /// The sidebar's plus: a new collection, then the two ways to bring one in. Import and
    /// Subscribe carry their subtitles, because which of the two to use is the one question the
    /// words alone do not answer. Built without being shown, so a test can read it.
    /// </summary>
    internal ContextMenu BuildSidebarMenu()
    {
        var menu = new ContextMenu { PlacementTarget = AddCollectionButton, Placement = PlacementMode.Bottom, StaysOpen = false };
        menu.Items.Add(Entry(CollectionsModel.NewButton, () => Act(Model.Create)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry(CollectionsModel.ImportButton, CollectionsModel.ImportSubtitle, () => Import(keepInSync: false)));
        menu.Items.Add(Entry(CollectionsModel.SubscribeButton, CollectionsModel.SubscribeSubtitle, () => Import(keepInSync: true)));
        return menu;
    }

    // MARK: collection header

    private void OnMore(object sender, RoutedEventArgs e) => BuildCollectionMenu().IsOpen = true;

    /// <summary>
    /// The header's More: the model's list, in its order. The entries that do not apply are
    /// already left out, and the two it dims arrive saying so. Built without being shown, so a
    /// test can read it.
    /// </summary>
    internal ContextMenu BuildCollectionMenu()
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Bottom, StaysOpen = false };
        foreach (var entry in Model.CollectionMenu)
        {
            if (entry is CollectionsModel.MenuEntry.Separator)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var item = Entry(CollectionsModel.Title(entry), () => Run(entry));
            item.IsEnabled = entry switch
            {
                CollectionsModel.MenuEntry.ExportAll exportAll => exportAll.Enabled,
                CollectionsModel.MenuEntry.Delete delete => delete.Enabled,
                _ => true,
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>
    /// One entry of the More menu. Both publish entries open the same dialog: a published
    /// collection's is its settings.
    /// </summary>
    private void Run(CollectionsModel.MenuEntry entry)
    {
        switch (entry)
        {
            case CollectionsModel.MenuEntry.MakeActive:
                if (Model.Selected is { } collection)
                {
                    Act(() => Model.SwitchTo(collection));
                }
                break;
            case CollectionsModel.MenuEntry.Rename:
                Act(Model.Rename);
                break;
            case CollectionsModel.MenuEntry.Duplicate:
                Act(() => Model.Duplicate());
                break;
            case CollectionsModel.MenuEntry.StartPublishing:
            case CollectionsModel.MenuEntry.PublishingSettings:
                PublishSelected();
                break;
            case CollectionsModel.MenuEntry.StopPublishing:
                Act(Model.StopPublishing);
                break;
            case CollectionsModel.MenuEntry.ShowPublishedFile:
                Reveal(Model.PublishedFilePath);
                break;
            case CollectionsModel.MenuEntry.ShowSourceFile:
                Reveal(Model.SourceFilePath);
                break;
            case CollectionsModel.MenuEntry.ExportAll:
                // The whole collection: the menu acts on the collection, not on what is ticked.
                if (Model.Selected is { } shown)
                {
                    PresentPublish(new PublishModel(state, shown), PublishDialogMode.Export);
                }
                break;
            case CollectionsModel.MenuEntry.MakeLocalCopy:
                Act(Model.MakeLocalCopy);
                break;
            case CollectionsModel.MenuEntry.Refresh:
                Act(Model.Refresh);
                break;
            case CollectionsModel.MenuEntry.StopSyncing:
                Act(Model.StopSyncing);
                break;
            case CollectionsModel.MenuEntry.Delete:
                Act(Model.Delete);
                break;
        }
    }

    /// <summary>
    /// The Publish dialog over the collection on show. Publishing binds the whole collection, so
    /// this one takes no subset. Shared by the More menu and a blocked publish's banner, so both
    /// open the same dialog the same way.
    /// </summary>
    private void PublishSelected()
    {
        if (Model.Selected is { } collection)
        {
            PresentPublish(new PublishModel(state, collection), PublishDialogMode.Publish);
        }
    }

    /// <summary>The Mac's "Show in Finder": the document selected in its folder.</summary>
    private static void Reveal(string? path)
    {
        if (path is null)
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // Settings' Show in Explorer does the same: explorer.exe failing to launch is not
            // worth a sentence of its own, so it is a silent no-op rather than a crash.
        }
    }

    // MARK: connector list header

    /// <summary>
    /// A new connector in the collection on show, through the same editor the pencil opens.
    /// </summary>
    private void OnAddConnector(object sender, RoutedEventArgs e) => windows.OpenEditor(Model.NewConnectorTarget());

    // MARK: selection bar

    private void OnCopyTo(object sender, RoutedEventArgs e) => BuildCopyMenu().IsOpen = true;

    /// <summary>
    /// Copy to: every other collection, a synced one listed but dimmed with the reason under its
    /// name — a read-only mirror cannot take copies, and the picker says so rather than the copy
    /// refusing after the fact — then New Collection. Opens upward, from the bottom of the window.
    /// Built without being shown, so a test can read it.
    /// </summary>
    internal ContextMenu BuildCopyMenu()
    {
        var menu = new ContextMenu { PlacementTarget = CopyToButton, Placement = PlacementMode.Top, StaysOpen = false };
        var destinations = Model.CopyDestinations;
        foreach (var destination in destinations)
        {
            var name = destination.Name;
            var item = destination.IsEnabled
                ? Entry(name, () => Copy(name))
                : Entry(name, CollectionsModel.ReadOnlyNote, () => { });
            item.IsEnabled = destination.IsEnabled;
            menu.Items.Add(item);
        }
        // A lone collection has nowhere else to copy to, and its menu does not start with a separator.
        if (destinations.Count > 0)
        {
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Entry(CollectionsModel.NewButton, () => Act(() => Model.CopyCheckedIntoNewCollection())));
        return menu;
    }

    /// <summary>Straight through when nothing clashes; otherwise the Copy dialog asks about the clashes first.</summary>
    private void Copy(string destination)
    {
        if (Model.CheckedNamesClashing(destination).Count == 0)
        {
            Act(() => Model.CopyChecked(destination));
        }
        else
        {
            Surfaces.ShowCopy(this, new CopyModel(Model, destination), () => Model.LastError);
        }
    }

    /// <summary>
    /// Export writes only the ticked connectors — what the bar's count says. The More menu's
    /// Export All takes the whole collection instead.
    /// </summary>
    private void OnExportChecked(object sender, RoutedEventArgs e)
    {
        if (Model.Selected is { } collection)
        {
            PresentPublish(new PublishModel(state, collection, Model.ExportIntentForChecked()), PublishDialogMode.Export);
        }
    }

    private void OnRemoveChecked(object sender, RoutedEventArgs e) => Act(Model.RemoveChecked);

    // MARK: menus

    private static MenuItem Entry(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// A two-line entry: the name, and under it the subtitle that tells it apart. A header built
    /// from elements gives the item nothing of its own to announce, so the item is given the name
    /// and the subtitle as its help text.
    /// </summary>
    private MenuItem Entry(string header, string subtitle, Action action)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = header });
        panel.Children.Add(new TextBlock { Text = subtitle, Style = (Style)FindResource("CaptionText") });
        var item = new MenuItem { Header = panel };
        AutomationProperties.SetName(item, header);
        AutomationProperties.SetHelpText(item, subtitle);
        item.Click += (_, _) => action();
        return item;
    }

    // MARK: panes

    /// <summary>
    /// The view-to-model half of the sidebar's selection, which the markup deliberately does not
    /// bind. Only a real collection, and only a different one, reaches the model: a selection the
    /// list cleared or re-resolved while its items were being replaced says nothing about which
    /// collection the user wants to see.
    /// </summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (writingSelection
            || Sidebar.SelectedValue is not string name
            || string.Equals(name, Model.Selected, StringComparison.Ordinal))
        {
            return;
        }
        writingSelection = true;
        try
        {
            Model.Selected = name;
        }
        finally
        {
            writingSelection = false;
        }
    }

    /// <summary>
    /// Double-clicking a collection makes it the active one; the click before it selected it.
    /// Resolved to the container the click landed on, so the empty space under the last item
    /// does nothing rather than activating whatever happens to be selected.
    /// </summary>
    private void OnActivate(object sender, MouseButtonEventArgs e)
    {
        // PreviewMouseDoubleClick fires for every button, and a double right-click over a row is
        // not a request to activate it.
        if (e.ChangedButton == MouseButton.Left
            && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(Sidebar, source) is ListBoxItem { DataContext: CollectionsModel.Item item })
        {
            Act(() => Model.SwitchTo(item.Name));
        }
    }

    /// <summary>
    /// The same action, labelled: the context menu is what a keyboard and a screen reader reach,
    /// and it acts on the collection it was raised over — which a right-click does not select.
    /// </summary>
    private void OnMakeActive(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is CollectionsModel.Item item)
        {
            Act(() => Model.SwitchTo(item.Name));
        }
    }

    /// <summary>
    /// Greyed out for the collection that is already active, as the Mac's menu item is. The
    /// answer comes from the parameter, never from a DataContext: one ContextMenu instance is
    /// shared by every container the item container style makes, so its inheritance context is
    /// whatever claimed it last.
    /// </summary>
    private void OnCanMakeActive(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = e.Parameter is CollectionsModel.Item { IsActive: false };

    private void OnRowTicked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: CollectionsModel.Row row } box)
        {
            Model.SetChecked(row.Name, box.IsChecked == true);
        }
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CollectionsModel.Row row })
        {
            windows.OpenEditor(Model.EditTargetFor(row.Name));
        }
    }

    // MARK: banner strip

    /// <summary>
    /// The strip's one button. True says the news needs nothing from the file system and the
    /// Review dialog is the whole answer; false says this window owes something else, and what is
    /// what the banner is: a file for a source to locate, a folder for a write that failed — the
    /// model refuses anything that is not what it asked for — or, for a publish stopped for
    /// review, the Publish dialog.
    /// </summary>
    private void OnBannerAction(object sender, RoutedEventArgs e)
    {
        if (Model.BannerAction())
        {
            if (Model.Selected is { } collection)
            {
                Review(collection);
            }
            return;
        }
        switch (state.CollectionBanner)
        {
            case CollectionBanner.Locate:
                if (Surfaces.ChooseDocument(this) is { } path)
                {
                    Report(Model.LocateSource(path));
                }
                break;
            case CollectionBanner.PublishFailed:
                if (Surfaces.ChooseFolder(this) is { } folder)
                {
                    Report(Model.ChoosePublishFolder(folder));
                }
                break;
            case CollectionBanner.PublishBlocked:
                // Stopped for review, not for a folder: another folder would re-bind the collection,
                // write nothing there and leave the old folder's document behind. The Publish
                // dialog is where the author answers it.
                PublishSelected();
                break;
        }
    }

    // MARK: requests

    /// <summary>
    /// Below layout and render, because both paths into it run at the wrong moment for a modal:
    /// <c>Loaded</c> fires inside <see cref="Window.Show"/>, before the registry has brought the
    /// window forward, and a change notification arrives wherever the setter was called. A
    /// picker opened from either would stand in front of a window that is not on screen yet.
    /// </summary>
    private void ScheduleConsume()
    {
        if (!closed)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Consume));
        }
    }

    /// <summary>What the flyout asked for, taken so nothing can act on it twice.</summary>
    private void Consume()
    {
        // Queued below layout, so this can run after the window has gone: leave the request for
        // whichever window opens next rather than taking it into a closed one.
        if (closed || state.TakeCollectionsWindowRequest() is not { } request)
        {
            return;
        }
        LastRequest = request;
        switch (request)
        {
            case CollectionsWindowRequest.ImportFile:
                Import(keepInSync: false);
                break;
            case CollectionsWindowRequest.ExportActive:
                // The menu item names the collection that is active now, not whichever one this
                // window last had selected — and it offers the whole of it, because a selection
                // that has just moved carries no ticks and an empty subset writes an empty
                // document.
                Model.Selected = state.ActiveCollection;
                PresentPublish(new PublishModel(state, state.ActiveCollection), PublishDialogMode.Export);
                break;
            case CollectionsWindowRequest.Review review:
                Review(review.Collection);
                break;
            case CollectionsWindowRequest.Publish publish:
                // A publish the app stopped for review, for the collection it names — which need
                // not be the one this window is showing. The dialog is where the author answers it.
                Model.Selected = publish.Collection;
                PresentPublish(new PublishModel(state, publish.Collection), PublishDialogMode.Publish);
                break;
        }
    }

    // MARK: dialogs

    /// <summary>
    /// Import and Subscribe are the same picker: one document, and what happens to it is the
    /// dialog's question rather than the picker's.
    /// </summary>
    private void Import(bool keepInSync)
    {
        if (Surfaces.ChooseDocument(this) is not { } path)
        {
            return;
        }
        var model = new ImportModel(state, path);
        // Subscribe is Import with the second mode already chosen: the picker that opened it
        // said which of the two the user asked for.
        if (keepInSync)
        {
            model.ImportMode = ImportModel.Mode.KeepInSync;
        }
        Surfaces.ShowImport(this, model);
    }

    private void PresentPublish(PublishModel model, PublishDialogMode mode) =>
        Surfaces.ShowPublish(this, model, mode);

    private void Review(string collection)
    {
        Model.Selected = collection;
        Surfaces.ShowReview(this, new ReviewModel(state, collection));
    }

    private static string? PickDocument(Window owner)
    {
        var picker = new OpenFileDialog { Title = "Choose", Filter = "Collection (*.json)|*.json", Multiselect = false };
        return picker.ShowDialog(owner) == true ? picker.FileName : null;
    }

    /// <summary>Settings ▸ Storage's picker, for the folder a failed publish asks to be pointed at.</summary>
    private static string? PickFolder(Window owner)
    {
        var picker = new OpenFolderDialog { Title = "Choose", Multiselect = false };
        return picker.ShowDialog(owner) == true ? picker.FolderName : null;
    }

    // MARK: refusals

    /// <summary>
    /// One collection action and the refusal it may leave behind. The model records it in
    /// <see cref="CollectionsModel.LastError"/> rather than publishing a line for it, and this
    /// window has no room for one, so it is said the way every other refusal here is said.
    /// </summary>
    private void Act(Action action)
    {
        action();
        Report(Model.LastError);
    }

    private void Report(string? failure)
    {
        if (failure is not null)
        {
            dialogs.Inform(failure, null);
        }
    }
}

/// <summary>
/// One header pill's words, from <see cref="CollectionsModel.Title(CollectionsModel.Pill)"/>; this
/// exists only because XAML cannot call a method.
/// </summary>
public sealed class CollectionPillTitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is CollectionsModel.Pill pill ? CollectionsModel.Title(pill) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// One sidebar collection's chain tooltip. The rule and the wording are both
/// <see cref="CollectionsModel.SyncedGlyphTooltip"/>'s, which answers null where there is no
/// chain to explain; this exists only because XAML cannot call a method.
/// </summary>
public sealed class CollectionSourceTooltipConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is CollectionsModel.Item item ? CollectionsModel.SyncedGlyphTooltip(item) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

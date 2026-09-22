using System.ComponentModel;
using System.Globalization;
using System.Windows;
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
/// right, the toolbar and the action links that act on it, and the three dialogs it puts in front
/// of itself. Layout, bindings, the native pickers and the one formatted button caption; every
/// rule and string is CollectionsModel's.
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
    /// The two pickers and the three dialogs this window puts in front of itself. One overridable
    /// bundle, because a test drives this window on the very dispatcher it lives on: a real modal
    /// would block the test that opened it, and a real picker would wait for a person.
    /// </summary>
    internal sealed record Presenters(
        Func<Window, string?> ChooseDocument,
        Func<Window, string?> ChooseFolder,
        Func<Window, ImportModel, bool> ShowImport,
        Func<Window, ReviewModel, bool> ShowReview,
        Func<Window, PublishModel, PublishDialogMode, bool> ShowPublish);

    internal static Presenters Live { get; } = new(
        PickDocument,
        PickFolder,
        (owner, model) => ImportDialog.Show(owner, model),
        (owner, model) => ReviewDialog.Show(owner, model),
        (owner, model, mode) => PublishDialog.Show(owner, model, mode));

    internal Presenters Surfaces { get; set; } = Live;

    /// <summary>
    /// The one caption built from a value rather than bound: the count beside Export is a format,
    /// and the ticks it counts are a plain field of a row that notifies nobody. Everything else
    /// on this window is a binding the model raises.
    /// </summary>
    private void Refresh() => ExportButton.Content = CollectionsModel.ExportButton(Model.CheckedNames.Count);

    // MARK: toolbar

    private void OnImport(object sender, RoutedEventArgs e) => Import(keepInSync: false);

    private void OnSubscribe(object sender, RoutedEventArgs e) => Import(keepInSync: true);

    /// <summary>
    /// Export writes only the ticked connectors — what the count beside the button says, and what
    /// <see cref="CollectionsModel.CanExport"/> waits for.
    /// </summary>
    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (Model.Selected is { } collection)
        {
            PresentPublish(new PublishModel(state, collection, Model.ExportIntentForChecked()), PublishDialogMode.Export);
        }
    }

    /// <summary>Publishing binds the whole collection, so this one takes no subset.</summary>
    private void OnPublish(object sender, RoutedEventArgs e)
    {
        if (Model.Selected is { } collection)
        {
            PresentPublish(new PublishModel(state, collection), PublishDialogMode.Publish);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Act(Model.Refresh);

    private void OnMakeLocalCopy(object sender, RoutedEventArgs e) => Act(Model.MakeLocalCopy);

    private void OnNew(object sender, RoutedEventArgs e) => Act(Model.Create);

    // MARK: action links

    private void OnRename(object sender, RoutedEventArgs e) => Act(Model.Rename);

    private void OnDelete(object sender, RoutedEventArgs e) => Act(Model.Delete);

    private void OnStopPublishing(object sender, RoutedEventArgs e) => Act(Model.StopPublishing);

    private void OnStopSyncing(object sender, RoutedEventArgs e) => Act(Model.StopSyncing);

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

    private void OnRowSwitched(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { DataContext: CollectionsModel.Row row } toggle)
        {
            Act(() => Model.SetEnabled(row.Name, toggle.IsChecked == true));
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
    /// Review dialog is the whole answer; false says this window owes a picker, and which one is
    /// what the banner is — the model then refuses anything that is not what it asked for.
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
        }
    }

    // MARK: dialogs

    /// <summary>
    /// Import… and Subscribe… are the same picker: one document, and what happens to it is the
    /// dialog's question rather than the picker's.
    /// </summary>
    private void Import(bool keepInSync)
    {
        if (Surfaces.ChooseDocument(this) is not { } path)
        {
            return;
        }
        var model = new ImportModel(state, path);
        // Subscribe… is Import… with the second mode already chosen: the picker that opened it
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

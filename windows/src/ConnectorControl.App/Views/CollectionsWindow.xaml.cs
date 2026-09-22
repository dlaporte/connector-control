using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
                Consume();
            }
        };
        state.PropertyChanged += onStateChanged;
        Loaded += (_, _) => Consume();
        Closed += (_, _) =>
        {
            state.PropertyChanged -= onStateChanged;
            Model.PropertyChanged -= onModelChanged;
            Model.Dispose();
        };
        Refresh();
    }

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

    private void OnExport(object sender, RoutedEventArgs e) => Publish(PublishDialogMode.Export);

    private void OnPublish(object sender, RoutedEventArgs e) => Publish(PublishDialogMode.Publish);

    private void OnRefresh(object sender, RoutedEventArgs e) => Act(Model.Refresh);

    private void OnMakeLocalCopy(object sender, RoutedEventArgs e) => Act(Model.MakeLocalCopy);

    private void OnNew(object sender, RoutedEventArgs e) => Act(Model.Create);

    // MARK: action links

    private void OnRename(object sender, RoutedEventArgs e) => Act(Model.Rename);

    private void OnDelete(object sender, RoutedEventArgs e) => Act(Model.Delete);

    private void OnStopPublishing(object sender, RoutedEventArgs e) => Act(Model.StopPublishing);

    private void OnStopSyncing(object sender, RoutedEventArgs e) => Act(Model.StopSyncing);

    // MARK: panes

    /// <summary>Double-clicking a collection makes it the active one; the click before it selected it.</summary>
    private void OnActivate(object sender, RoutedEventArgs e)
    {
        if (Sidebar.SelectedValue is string name)
        {
            Act(() => Model.SwitchTo(name));
        }
    }

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

    /// <summary>What the flyout asked for, taken so nothing can act on it twice.</summary>
    private void Consume()
    {
        if (state.TakeCollectionsWindowRequest() is not { } request)
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
                // window last had selected.
                Model.Selected = state.ActiveCollection;
                Publish(PublishDialogMode.Export);
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

    private void Publish(PublishDialogMode mode)
    {
        if (Model.Selected is { } collection)
        {
            Surfaces.ShowPublish(this, new PublishModel(state, collection), mode);
        }
    }

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

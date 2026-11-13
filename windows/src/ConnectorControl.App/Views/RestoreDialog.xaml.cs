using System.Windows;
using System.Windows.Controls;
using ConnectorControl.Core.State;

namespace ConnectorControl.App.Views;

/// <summary>Modal, 460 wide, two tabs: Collections (a 120-high list of master-list backups over a 120-high list of
/// the chosen one's collections) and Claude's config (a 240-high list of backups); under them the chosen tab's
/// inline error and Cancel / Restore, then its confirmation.</summary>
public partial class RestoreDialog : DialogWindow
{
    /// <summary>True while the collections list is being refilled from the model, whose choice it shows rather than
    /// changes.</summary>
    private bool showingRows;

    public RestoreDialog(AppState state)
    {
        InitializeComponent();
        var dialogs = new WpfDialogs(() => this);
        Model = new RestoreModel(state, dialogs);
        Collections = new CollectionRestoreModel(state, dialogs);
        // Both tabs read their backups once, when the dialog opens, not each time a tab is shown.
        Collections.Load();
        Model.Load();
        CollectionBackupList.ItemsSource = Collections.BackupLabels;
        BackupList.ItemsSource = Model.BackupNames;
        ShowRows();
        Collections.PropertyChanged += (_, e) =>
        {
            if (ObservableObject.Affects(e, nameof(CollectionRestoreModel.Rows)))
            {
                ShowRows();
            }
        };
        Tabs.SelectedItem = CollectionsTabItem;
        Footer.DataContext = Collections;
        // Subscribed only now: the TabControl picks its first tab while it loads, before the models exist.
        Tabs.SelectionChanged += OnTabChanged;
        this.CloseWhenAsked(handler => Model.CloseRequested += handler);
        this.CloseWhenAsked(handler => Collections.CloseRequested += handler);
    }

    /// <summary>The Claude's config tab.</summary>
    public RestoreModel Model { get; }

    /// <summary>The Collections tab.</summary>
    public CollectionRestoreModel Collections { get; }

    private bool OnCollectionsTab => ReferenceEquals(Tabs.SelectedItem, CollectionsTabItem);

    public static void Show(Window? owner, AppState state) => WpfDialogs.Present(new RestoreDialog(state), owner);

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // The lists' own selection changes bubble up through the TabControl too.
        if (!ReferenceEquals(e.OriginalSource, Tabs))
        {
            return;
        }
        Footer.DataContext = OnCollectionsTab ? Collections : Model;
    }

    /// <summary>The chosen backup's collections, with the model's chosen one still chosen: a refusal re-reads how each
    /// stands and keeps the choice, as the Mac's list keeps it.</summary>
    private void ShowRows()
    {
        showingRows = true;
        try
        {
            CollectionList.ItemsSource = Collections.Rows;
            CollectionList.SelectedItem = Collections.Rows.FirstOrDefault(row => string.Equals(row.Name, Collections.Collection, StringComparison.Ordinal));
        }
        finally
        {
            showingRows = false;
        }
    }

    private void OnCollectionBackupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = CollectionBackupList.SelectedIndex;
        Collections.Selection = index >= 0 && index < Collections.Backups.Count ? Collections.Backups[index].Path : null;
    }

    private void OnCollectionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!showingRows)
        {
            Collections.Collection = (CollectionList.SelectedItem as CollectionRestoreModel.Row)?.Name;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = BackupList.SelectedIndex;
        Model.Selection = index >= 0 && index < Model.Backups.Count ? Model.Backups[index] : null;
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (OnCollectionsTab)
        {
            Collections.Restore();
        }
        else
        {
            Model.Restore();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (OnCollectionsTab)
        {
            Collections.Cancel();
        }
        else
        {
            Model.Cancel();
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using ConnectorControl.Core.State;

namespace ConnectorControl.App.Views;

/// <summary>Catalog §5: modal, 460 wide, a 180-high list of backups, Cancel / Restore…, confirmation, inline error.</summary>
public partial class RestoreDialog : Window
{
    public RestoreDialog(AppState state)
    {
        InitializeComponent();
        Model = new RestoreModel(state, new WpfDialogs(() => this));
        Model.Load();
        DataContext = Model;
        BackupList.ItemsSource = Model.BackupNames;
        Model.CloseRequested += () => Dispatcher.BeginInvoke(new Action(Close));
    }

    public RestoreModel Model { get; }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = BackupList.SelectedIndex;
        Model.Selection = index >= 0 && index < Model.Backups.Count ? Model.Backups[index] : null;
    }

    private void OnRestore(object sender, RoutedEventArgs e) => Model.Restore();

    private void OnCancel(object sender, RoutedEventArgs e) => Model.Cancel();
}

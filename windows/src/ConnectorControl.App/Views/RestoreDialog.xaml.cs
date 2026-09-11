using System.Windows;
using System.Windows.Controls;
using ConnectorControl.Core.State;

namespace ConnectorControl.App.Views;

/// <summary>Modal, 460 wide, a 180-high list of backups, Cancel / Restore…, confirmation, inline error.</summary>
public partial class RestoreDialog : DialogWindow
{
    public RestoreDialog(AppState state)
    {
        InitializeComponent();
        Model = new RestoreModel(state, new WpfDialogs(() => this));
        Model.Load();
        DataContext = Model;
        BackupList.ItemsSource = Model.BackupNames;
        CloseWhenModelAsks(handler => Model.CloseRequested += handler);
    }

    public RestoreModel Model { get; }

    public static void Show(Window? owner, AppState state) => WpfDialogs.Present(new RestoreDialog(state), owner);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = BackupList.SelectedIndex;
        Model.Selection = index >= 0 && index < Model.Backups.Count ? Model.Backups[index] : null;
    }

    private void OnRestore(object sender, RoutedEventArgs e) => Model.Restore();

    private void OnCancel(object sender, RoutedEventArgs e) => Model.Cancel();
}

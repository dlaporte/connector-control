using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ConnectorControl.Core;
using ConnectorControl.Core.State;

namespace ConnectorControl.App.Views;

/// <summary>
/// Min 540×620, one window per target id (WindowRegistry
/// enforces that), Enter saves, Escape cancels, dialogs owned by this window.
/// </summary>
public partial class EditorWindow : Window
{
    /// <summary>New remote connectors on Windows use the cmd /c npx bridge shape.</summary>
    public const RemoteLaunchStyle NewRemoteStyle = RemoteLaunchStyle.CmdNpx;

    public EditorWindow(AppState state, EditTarget target)
    {
        InitializeComponent();
        Model = new EditorModel(state, target, new WpfDialogs(() => this), NewRemoteStyle);
        DataContext = Model;
        Title = Model.WindowTitle;
        Model.CloseRequested += () => Dispatcher.BeginInvoke(new Action(Close));
        Model.FocusEnvRowRequested += row => Dispatcher.BeginInvoke(new Action(() => FocusEnvRow(row)), DispatcherPriority.Loaded);
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) => Model.Dispose();   // stop listening to AppState.ToolStatuses
    }

    public EditorModel Model { get; }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Model.Cancel();
            e.Handled = true;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e) => Model.Save();

    private void OnCancel(object sender, RoutedEventArgs e) => Model.Cancel();

    private void OnRemove(object sender, RoutedEventArgs e) => Model.Remove();

    private void OnAddArg(object sender, RoutedEventArgs e) => Model.AddArg();

    private void OnRemoveArg(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ArgRow row)
        {
            Model.RemoveArg(row);
        }
    }

    private void OnAddEnv(object sender, RoutedEventArgs e) => Model.AddEnvRow();

    private void OnRemoveEnv(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is EnvRow row)
        {
            Model.RemoveEnvRow(row);
        }
    }

    private void OnToggleReveal(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is EnvRow row)
        {
            Model.ToggleReveal(row);
        }
    }

    /// <summary>A fresh ＋ row focuses its name field on the next turn.</summary>
    private void FocusEnvRow(EnvRow row)
    {
        EnvList.UpdateLayout();
        if (EnvList.ItemContainerGenerator.ContainerFromItem(row) is DependencyObject container
            && VisualTree.FindDescendant<TextBox>(container) is { } nameBox)
        {
            nameBox.Focus();
        }
    }
}

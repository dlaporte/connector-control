using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    private readonly AppState state;

    public EditorWindow(AppState state, EditTarget target)
    {
        InitializeComponent();
        this.state = state;
        Model = new EditorModel(state, target, new WpfDialogs(() => this), NewRemoteStyle);
        DataContext = Model;
        Title = Model.WindowTitle;
        Model.CloseRequested += () => Dispatcher.BeginInvoke(new Action(Close));
        Model.FocusEnvRowRequested += row => Dispatcher.BeginInvoke(new Action(() => FocusEnvRow(row)), DispatcherPriority.Loaded);
        PreviewKeyDown += OnPreviewKeyDown;
        // The model watches AppState for everything the collection decides and raises it, so the
        // window has nothing of its own to subscribe to and nothing to re-seat.
        Closed += (_, _) => Model.Dispose();
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

    /// <summary>Clicking the link shows the same answer the button's tooltip gives on hover.</summary>
    private void OnWhatCanIChange(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).ToolTip is ToolTip tip)
        {
            tip.PlacementTarget = (UIElement)sender;
            tip.IsOpen = true;
        }
    }

    /// <summary>
    /// The local collections this connector can be copied into, as a menu under the button. Its
    /// own collection is synced, so it is never among them; the filter says so rather than
    /// relying on it.
    /// </summary>
    private void OnMakeLocalCopy(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Top };
        foreach (var name in state.LocalCollectionNames.Where(n => n != Model.CollectionName))
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => MakeLocalCopy(name);
            menu.Items.Add(item);
        }
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>
    /// The one failure AppState reports here is a target that is not local, which a menu built
    /// from the local collections cannot offer.
    /// </summary>
    internal void MakeLocalCopy(string collection)
    {
        if (Model.MakeLocalCopy(collection) is null)
        {
            Close();
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

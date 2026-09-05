using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ConnectorControl.Core;
using ConnectorControl.Core.State;

namespace ConnectorControl.App.Views;

/// <summary>
/// Catalog §3 / spec §7.2: min 540×620, one window per target id (WindowRegistry
/// enforces that), Enter saves, Escape cancels, dialogs owned by this window.
/// </summary>
public partial class EditorWindow : Window
{
    /// <summary>Spec §5.5: new remote connectors on Windows use the cmd /c npx bridge shape.</summary>
    public const RemoteLaunchStyle NewRemoteStyle = RemoteLaunchStyle.CmdNpx;

    public EditorWindow(AppState state, EditTarget target)
    {
        InitializeComponent();
        Model = new EditorModel(state, target, new WpfDialogs(() => this), NewRemoteStyle);
        DataContext = Model;
        Title = Model.WindowTitle;
        Model.CloseRequested += () => Dispatcher.BeginInvoke(new Action(Close));
        Model.FocusEnvRowRequested += row => Dispatcher.BeginInvoke(new Action(() => FocusEnvRow(row)), DispatcherPriority.Loaded);
        Model.PropertyChanged += OnModelPropertyChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public EditorModel Model { get; }

    public string TargetId => Model.Target.Id;

    /// <summary>
    /// Task 7 review: EditorModel.IsFormView/IsJsonView can refuse a switch (e.g. invalid JSON)
    /// by raising PropertyChanged for IsFormView/IsJsonView synchronously, from inside their own
    /// setter. A WPF TwoWay binding ignores a change notification for the same property it is
    /// currently writing, so the segmented control's RadioButton would stay checked instead of
    /// snapping back to reflect the refusal. Force each toggle to re-read the model once the
    /// current binding update has finished, at DataBind priority (below the Normal/Send priority
    /// the binding update itself runs at, so it runs after — but still before layout/render).
    /// </summary>
    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorModel.IsFormView) or nameof(EditorModel.IsJsonView))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
            {
                BindingOperations.GetBindingExpression(FormToggle, ToggleButton.IsCheckedProperty)?.UpdateTarget();
                BindingOperations.GetBindingExpression(JsonToggle, ToggleButton.IsCheckedProperty)?.UpdateTarget();
            });
        }
    }

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

    /// <summary>Catalog §3.6: a fresh ＋ row focuses its name field on the next turn.</summary>
    private void FocusEnvRow(EnvRow row)
    {
        EnvList.UpdateLayout();
        if (EnvList.ItemContainerGenerator.ContainerFromItem(row) is DependencyObject container
            && FindDescendant<TextBox>(container) is { } nameBox)
        {
            nameBox.Focus();
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }
            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }
        return null;
    }
}

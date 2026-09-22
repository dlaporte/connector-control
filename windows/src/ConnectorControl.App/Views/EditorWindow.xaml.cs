using System.ComponentModel;
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
    private (bool ReadOnly, string? Note) collectionFacts;

    /// <summary>
    /// What the collection's document asked this machine for, as of the last time the form's shape
    /// could change. The model's placeholder flags are deliberately live — a field stops asking
    /// the moment it is filled — which is what the caution mark and the hint want, and the
    /// opposite of what the field itself can stand: a value going in must not disable the box it
    /// is being typed into, or swap it for another control mid-keystroke.
    /// </summary>
    private readonly HashSet<object> asked = [];

    public EditorWindow(AppState state, EditTarget target)
    {
        InitializeComponent();
        this.state = state;
        Model = new EditorModel(state, target, new WpfDialogs(() => this), NewRemoteStyle);
        TakeSnapshot();
        DataContext = Model;
        collectionFacts = (Model.IsReadOnly, Model.HeaderNote);
        Title = Model.WindowTitle;
        Model.CloseRequested += () => Dispatcher.BeginInvoke(new Action(Close));
        Model.FocusEnvRowRequested += row => Dispatcher.BeginInvoke(new Action(() => FocusEnvRow(row)), DispatcherPriority.Loaded);
        PreviewKeyDown += OnPreviewKeyDown;
        state.PropertyChanged += OnStateChanged;
        Closed += (_, _) =>
        {
            state.PropertyChanged -= OnStateChanged;
            Model.Dispose();   // stop listening to AppState.ToolStatuses
        };
    }

    public EditorModel Model { get; }

    /// <summary>Whether the document asks this machine for this row. See <see cref="asked"/> for
    /// why that is not the live flag.</summary>
    internal bool AskedFor(object? key) => key is not null && asked.Contains(key);

    private void TakeSnapshot()
    {
        asked.Clear();
        foreach (var row in Model.EnvRows)
        {
            if (Model.IsPlaceholder(row))
            {
                asked.Add(row);
            }
        }
        foreach (var index in Model.ArgsWithPlaceholders)
        {
            asked.Add(Model.Args[index]);
        }
    }

    /// <summary>
    /// The editor model republishes on tool statuses alone, but its header, its locks and its
    /// placeholder hints all read AppState — so a collection that stops syncing while this window
    /// is open has to repaint it from here. Re-seating the DataContext is what makes every binding
    /// re-read the model; the guard keeps that to a change which actually moves one of those two
    /// facts, so a finished tool probe cannot rebuild the form under the user's hands.
    ///
    /// The snapshot is retaken with it. It exists to keep a field steady while the user types into
    /// it, and that is only worth doing while the form is the collection author's; once the
    /// collection stops syncing, an unmasked marker box has become an ordinary secret field and
    /// has to go back to being masked.
    /// </summary>
    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        var facts = (Model.IsReadOnly, Model.HeaderNote);
        if (facts == collectionFacts)
        {
            return;
        }
        collectionFacts = facts;
        TakeSnapshot();
        DataContext = null;
        DataContext = Model;
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

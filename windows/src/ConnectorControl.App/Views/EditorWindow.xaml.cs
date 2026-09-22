using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
    /// What the collection's document asked this machine for when the window opened, by row
    /// object and by auth-field name. The model's placeholder flags are deliberately live — a
    /// field stops asking the moment it is filled — which is what the caution mark and the hint
    /// want, and the opposite of what the field itself can stand: a value going in must not
    /// disable the box it is being typed into, or swap it for another control mid-keystroke.
    /// </summary>
    private readonly HashSet<object> asked = [];

    public EditorWindow(AppState state, EditTarget target)
    {
        InitializeComponent();
        this.state = state;
        Model = new EditorModel(state, target, new WpfDialogs(() => this), NewRemoteStyle);
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
        if (Model.BearerTokenIsPlaceholder)
        {
            asked.Add(nameof(EditorModel.BearerToken));
        }
        if (Model.HeaderValueIsPlaceholder)
        {
            asked.Add(nameof(EditorModel.HeaderValue));
        }
        if (Model.ClientSecretIsPlaceholder)
        {
            asked.Add(nameof(EditorModel.OAuthClientSecret));
        }
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

    /// <summary>Whether the document asked this machine for this row or auth field when the
    /// window opened. See <see cref="asked"/> for why that is not the live flag.</summary>
    internal bool AskedFor(object? key) => key is not null && asked.Contains(key);

    /// <summary>
    /// The editor model republishes on tool statuses alone, but its header, its locks and its
    /// placeholder hints all read AppState — so a collection that stops syncing while this window
    /// is open has to repaint it from here. Re-seating the DataContext is what makes every binding
    /// re-read the model; the guard keeps that to a change which actually moves one of those two
    /// facts, so a finished tool probe cannot rebuild the form under the user's hands.
    /// </summary>
    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        var facts = (Model.IsReadOnly, Model.HeaderNote);
        if (facts == collectionFacts)
        {
            return;
        }
        collectionFacts = facts;
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

/// <summary>A locked field's IsEnabled, from EditorModel.IsReadOnly.</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// Which of the header lines this element belongs to: EditorModel.HeaderState is a record, which a
/// DataTrigger cannot match, so the parameter names the state and the binding answers with a
/// visibility.
/// </summary>
public sealed class HeaderStateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = value is EditorModel.HeaderState.Synced ? "Synced"
            : value is EditorModel.HeaderState.Published ? "Published"
            : value is EditorModel.HeaderState.Imported ? "Imported"
            : "None";
        return kind == (string?)parameter ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The parts of a synced collection's rules that no single binding can reach: EditorModel answers
/// them per env row, per argument index or per decoded auth field, a DataTemplate's own
/// DataContext is the row, and a placeholder flag is derived from a field's text rather than
/// raised alongside it.
///
/// The first bound value is always the EditorWindow, which carries both the model and the snapshot
/// of what the document asked for at open. A row form passes the row itself next; a field form
/// names the field in the parameter, after a colon. Every further bound value is there only so the
/// binding re-evaluates as the text is typed into.
///
/// Which field is live, and which control renders it, comes from the snapshot, so that filling a
/// value cannot disable or replace the box it is going into. The caution mark and the hint come
/// from the live flag, so they clear the moment the value is in.
/// </summary>
public sealed class EditorRuleConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not EditorWindow window)
        {
            return DependencyProperty.UnsetValue;
        }
        var model = window.Model;
        if ((string?)parameter == "TipVisibility")
        {
            // ShowJsonTip is not raised when the JSON error moves, so the binding carries
            // HasJsonError alongside it purely to be told when to ask again.
            return model.ShowJsonTip ? Visibility.Visible : Visibility.Collapsed;
        }
        var parts = ((string?)parameter ?? string.Empty).Split(':');
        var field = parts.Length > 1;
        var key = field ? parts[1] : values[1];
        var (live, hint) = field ? Field(model, parts[1]) : Row(model, values[1]);
        return parts[0] switch
        {
            "IsLive" => !model.IsReadOnly || window.AskedFor(key),
            "PlaceholderVisibility" => window.AskedFor(key) ? Visibility.Visible : Visibility.Collapsed,
            "FilledVisibility" => window.AskedFor(key) ? Visibility.Collapsed : Visibility.Visible,
            "MarkThickness" => new Thickness(live ? 1 : 0),
            "Hint" => hint ?? string.Empty,
            "HintVisibility" => live && !string.IsNullOrEmpty(hint) ? Visibility.Visible : Visibility.Collapsed,
            _ => DependencyProperty.UnsetValue,
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static (bool Live, string? Hint) Row(EditorModel model, object? row)
    {
        switch (row)
        {
            case EnvRow env:
                return (model.IsPlaceholder(env), model.PlaceholderHint(env));
            case ArgRow arg:
                // The model indexes arguments by position; an ItemsControl hands over the row.
                var index = model.Args.IndexOf(arg);
                return (model.ArgsWithPlaceholders.Contains(index), model.PlaceholderHintForArg(index));
            default:
                return (false, null);
        }
    }

    private static (bool Live, string? Hint) Field(EditorModel model, string field) => field switch
    {
        nameof(EditorModel.BearerToken) => (model.BearerTokenIsPlaceholder, model.BearerTokenHint),
        nameof(EditorModel.HeaderValue) => (model.HeaderValueIsPlaceholder, model.HeaderValueHint),
        nameof(EditorModel.OAuthClientSecret) => (model.ClientSecretIsPlaceholder, model.ClientSecretHint),
        _ => (false, null),
    };
}

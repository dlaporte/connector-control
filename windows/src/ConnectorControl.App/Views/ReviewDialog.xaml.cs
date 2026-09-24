using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using ConnectorControl.Core.State;

namespace ConnectorControl.App.Views;

/// <summary>
/// The Review &amp; Apply sheet: what one synced collection's source would change, connector by
/// connector, with the JSON both sides of every change. Layout and bindings only; every rule and
/// string is ReviewModel's.
/// </summary>
public partial class ReviewDialog : DialogWindow
{
    /// <summary>Each kind's place in <see cref="ReviewModel.Kinds"/>, which is the order the groups show in.</summary>
    private static readonly Dictionary<ReviewModel.Kind, int> KindOrder =
        ReviewModel.Kinds.Select((kind, index) => (kind, index)).ToDictionary(pair => pair.kind, pair => pair.index);

    private readonly CollectionViewSource grouped = new();
    private readonly PropertyChangedEventHandler onModelChanged;

    public ReviewDialog(ReviewModel model)
    {
        InitializeComponent();
        Model = model;
        DataContext = model;
        Title = model.SheetTitle;
        grouped.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ReviewModel.Row.Kind)));
        Rebind();
        onModelChanged = (_, _) => Rebind();
        Model.PropertyChanged += onModelChanged;
        Closed += (_, _) => Model.PropertyChanged -= onModelChanged;
    }

    public ReviewModel Model { get; }

    /// <summary>
    /// True once the update has landed. The sheet reports through this rather than DialogResult,
    /// which can only be set on a window shown as a dialog — NamePromptDialog answers the same way.
    /// </summary>
    public bool Accepted { get; private set; }

    public static bool Show(Window? owner, ReviewModel model)
    {
        var dialog = new ReviewDialog(model);
        return Present(dialog, owner, () => dialog.Accepted);
    }

    /// <summary>
    /// The grouped view over the rows as they now stand. Re-reading the source replaces the row
    /// list outright, and a view belongs to the collection it was made for, so both are rebuilt
    /// together whenever the model says anything changed. A group is made where its kind first
    /// appears, so the rows go in sorted by <see cref="ReviewModel.Kinds"/> — a stable sort, which
    /// keeps the model's order within a kind — and the groups come out in the model's order, as
    /// the Mac sheet's do, whatever order the rows arrive in.
    /// </summary>
    private void Rebind()
    {
        grouped.Source = Model.Rows.OrderBy(row => KindOrder[row.Kind]).ToList();
        ChangeList.ItemsSource = grouped.View;
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        Model.Refresh();
        ShowFailure(FailureText, null);
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        var failure = Model.Apply();
        // A document that changed under the sheet already has the caution line above the footer,
        // carrying this very sentence and the Refresh button that answers it; it is not said twice.
        ShowFailure(FailureText, Model.SourceMoved ? null : failure);
        if (failure is null)
        {
            Accepted = true;
            Close();
        }
    }
}

/// <summary>
/// A group heading: which of the three kinds the rows under it are. The grouped view's key is the
/// kind itself, and <see cref="ReviewModel.KindLabel"/> is a method rather than a property, which
/// is the whole of what this converter is for.
/// </summary>
public sealed class ReviewKindLabelConverter : OneWayConverter<ReviewModel.Kind>
{
    protected override object? Map(ReviewModel.Kind kind, object? parameter) => ReviewModel.KindLabel(kind);
}

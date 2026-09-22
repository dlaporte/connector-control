using System.ComponentModel;
using System.Globalization;
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
    /// True once the update has landed. The sheet reports through this rather than DialogResult
    /// because Apply closes it from code, and DialogResult can only be set on a window that was
    /// shown as a dialog — which is how it ships, but not how a test can drive it.
    /// </summary>
    public bool Applied { get; private set; }

    public static bool Show(Window? owner, ReviewModel model)
    {
        var dialog = new ReviewDialog(model);
        return Present(dialog, owner, () => dialog.Applied);
    }

    /// <summary>
    /// The grouped view over the rows as they now stand. Re-reading the source replaces the row
    /// list outright, and a view belongs to the collection it was made for, so both are rebuilt
    /// together whenever the model says anything changed.
    /// </summary>
    private void Rebind()
    {
        grouped.Source = Model.Rows;
        ChangeList.ItemsSource = grouped.View;
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Model.Refresh();

    private void OnApply(object sender, RoutedEventArgs e)
    {
        // The one thing that refuses is a document that changed under the sheet, and the caution
        // line above the footer is what says so.
        if (!Model.Apply())
        {
            return;
        }
        Applied = true;
        Close();
    }
}

/// <summary>
/// A group heading: which of the three kinds the rows under it are. ReviewModel carries one label
/// per kind rather than a mapping, so the pairing lives here, beside the list that needs it.
/// </summary>
public sealed class ReviewKindLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ReviewModel.Kind kind ? Label(kind) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static string Label(ReviewModel.Kind kind) => kind switch
    {
        ReviewModel.Kind.Added => ReviewModel.AddedLabel,
        ReviewModel.Kind.Removed => ReviewModel.RemovedLabel,
        ReviewModel.Kind.Changed => ReviewModel.ChangedLabel,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

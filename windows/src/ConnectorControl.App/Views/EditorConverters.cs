using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ConnectorControl.Core.State;

namespace ConnectorControl.App.Views;

/// <summary>
/// Which of the header lines an element belongs to: EditorModel.HeaderState is a record, which a
/// DataTrigger cannot match, so the parameter names the state and the binding answers with a
/// visibility. Goes away once the model carries IsSynced / IsPublished / IsImported.
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

/// <summary>The caution ring round a value this machine still owes: one pixel, or none.</summary>
public sealed class MarkThicknessConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness(value is true ? 1 : 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The per-row half of a synced collection's rules. EditorModel answers those per env row and per
/// argument index, and a DataTemplate's own DataContext is the row — so a plain binding cannot ask
/// the question. Every other part of the editor's collection state is a plain binding on the
/// model.
///
/// The bound values are the EditorWindow, which carries both the model and the snapshot of what
/// the document asked for when it opened, then the row, then the row's value — the last only so
/// that the binding re-evaluates as the value is typed into.
///
/// Which control renders a row, and whether it is live, come from the snapshot, so that filling a
/// value cannot disable or replace the box it is going into. The caution ring and the hint follow
/// the value: the ring stands while the value is still owed (a marker, or emptied), and the hint
/// is whatever the author said about finding it.
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
        var row = values[1];
        var asks = window.AskedFor(row);
        return (string?)parameter switch
        {
            "PlaceholderVisibility" => asks ? Visibility.Visible : Visibility.Collapsed,
            "FilledVisibility" => asks ? Visibility.Collapsed : Visibility.Visible,
            // A field the user has emptied still owes the value the document asked for, so it
            // keeps its mark; one that now holds a real value does not.
            "OwedThickness" => new Thickness(asks && Owed(model, row) ? 1 : 0),
            "OwedVisibility" => asks && Owed(model, row) ? Visibility.Visible : Visibility.Collapsed,
            "Hint" => Hint(model, row) ?? string.Empty,
            _ => DependencyProperty.UnsetValue,
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static bool Owed(EditorModel model, object? row) => row switch
    {
        EnvRow env => model.IsPlaceholder(env) || env.Value.Length == 0,
        ArgRow arg => model.ArgsWithPlaceholders.Contains(model.Args.IndexOf(arg)) || arg.Value.Length == 0,
        _ => false,
    };

    private static string? Hint(EditorModel model, object? row) => row switch
    {
        EnvRow env => model.PlaceholderHint(env),
        // The model indexes arguments by position; an ItemsControl hands over the row.
        ArgRow arg => model.PlaceholderHintForArg(model.Args.IndexOf(arg)),
        _ => null,
    };
}

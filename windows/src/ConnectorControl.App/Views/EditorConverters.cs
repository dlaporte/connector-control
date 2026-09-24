using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ConnectorControl.Core.State;

namespace ConnectorControl.App.Views;

/// <summary>
/// The parts of a collection's rules that no plain binding can reach, because EditorModel answers
/// them per env row, per argument index or per named auth field: a DataTemplate's own DataContext
/// is the row, the model indexes arguments by position, and XAML cannot pick a property by name.
/// Everything else the editor needs is a plain binding on the model.
///
/// The bound values are the model, then the row (or, for an auth field, that field's text, with
/// the parameter naming it after a colon). A row's value, and an auth field's text, are bound only
/// so the binding re-evaluates as they are typed into: the answers are all the model's.
///
/// Both forms answer the same three questions, so a row and a decoded secret behave alike: whether
/// the document asks this machine for the field (fixed when the window opened, so that filling a
/// value cannot disable or replace the box it is going into), whether the value is still owed
/// (EditorModel's IsOwed rule), and what the author said about it.
/// </summary>
public sealed class EditorRuleConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not EditorModel model)
        {
            return DependencyProperty.UnsetValue;
        }
        var parts = ((string?)parameter ?? string.Empty).Split(':');
        var answer = parts.Length > 1 ? Field(model, parts[1]) : Row(model, values[1]);
        return parts[0] switch
        {
            "Live" => !model.IsReadOnly || answer.Asks,
            "PlaceholderVisibility" => answer.Asks ? Visibility.Visible : Visibility.Collapsed,
            "FilledVisibility" => answer.Asks ? Visibility.Collapsed : Visibility.Visible,
            "OwedThickness" => new Thickness(answer.Owed ? 1 : 0),
            "OwedVisibility" => answer.Owed ? Visibility.Visible : Visibility.Collapsed,
            "Hint" => answer.Hint ?? string.Empty,
            "PublishedHint" => answer.Published ?? string.Empty,
            _ => DependencyProperty.UnsetValue,
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private readonly record struct Answer(bool Asks, bool Owed, string? Hint, string? Published);

    /// <summary>Whether a value is still owed is the model's rule; this only asks it per row.</summary>
    private static Answer Row(EditorModel model, object? row)
    {
        switch (row)
        {
            case EnvRow env:
                return new(model.AsksFor(env), model.IsOwed(env), model.PlaceholderHint(env), model.PublishedHint(env));
            case ArgRow arg:
                // The model indexes arguments by position; an ItemsControl hands over the row.
                var index = model.Args.IndexOf(arg);
                return new(model.AsksForArg(index), model.IsOwedArg(index),
                           model.PlaceholderHintForArg(index), model.PublishedHintForArg(index));
            default:
                return default;
        }
    }

    /// <summary>
    /// A decoded auth field. Publishing strips values by env-var name and by argument pointer, so
    /// there is no published hint for one of these.
    /// </summary>
    private static Answer Field(EditorModel model, string field) => field switch
    {
        nameof(EditorModel.BearerToken) =>
            new(model.AsksForBearerToken, model.BearerTokenOwed, model.BearerTokenHint, null),
        nameof(EditorModel.HeaderValue) =>
            new(model.AsksForHeaderValue, model.HeaderValueOwed, model.HeaderValueHint, null),
        nameof(EditorModel.OAuthClientSecret) =>
            new(model.AsksForClientSecret, model.ClientSecretOwed, model.ClientSecretHint, null),
        _ => default,
    };
}

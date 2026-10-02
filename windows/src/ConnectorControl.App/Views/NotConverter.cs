using System.Globalization;
using System.Windows.Data;

namespace ConnectorControl.App.Views;

/// <summary>
/// The negation of a bound bool, for the common case of a control that is live exactly when some
/// flag is false — a field locked by EditorModel.IsReadOnly, say. An unset or non-bool value reads
/// as false, so a binding that has not resolved yet leaves its control enabled.
/// </summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

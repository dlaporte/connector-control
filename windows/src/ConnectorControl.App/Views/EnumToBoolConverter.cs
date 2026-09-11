using System.Globalization;
using System.Windows.Data;

namespace ConnectorControl.App.Views;

/// <summary>
/// Binds a segmented control's RadioButtons to one enum-valued property: each button's
/// ConverterParameter names the enum value it represents, so IsChecked reads true only for the
/// button matching the current value. A RadioButton's own grouping unchecks its siblings, so only
/// the button switching to true ever writes back.
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.Equals(Enum.Parse(value.GetType(), (string)parameter!));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Enum.Parse(targetType, (string)parameter!) : Binding.DoNothing;
}

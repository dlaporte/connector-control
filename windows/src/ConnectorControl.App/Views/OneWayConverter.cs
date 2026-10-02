using System.Globalization;
using System.Windows.Data;

namespace ConnectorControl.App.Views;

/// <summary>
/// A one-way converter over one bound type, for the model statics XAML cannot call: a value of
/// type <typeparamref name="T"/> is mapped, anything else — a container WPF has let go, a binding
/// not resolved yet — is <see cref="Fallback"/>, and nothing is ever written back.
/// </summary>
public abstract class OneWayConverter<T> : IValueConverter
{
    /// <summary>What a value of any other type shows: nothing to read, by default.</summary>
    protected virtual object? Fallback => string.Empty;

    protected abstract object? Map(T value, object? parameter);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is T typed ? Map(typed, parameter) : Fallback;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

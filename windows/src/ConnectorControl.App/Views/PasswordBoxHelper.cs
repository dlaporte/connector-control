using System.Windows;
using System.Windows.Controls;

namespace ConnectorControl.App.Views;

/// <summary>PasswordBox.Password is not bindable (by design); this attached property bridges it two-way.</summary>
public static class PasswordBoxHelper
{
    static PasswordBoxHelper()
    {
        // The box → model direction is wired here, once for the class, and NOT from
        // OnBoundPasswordChanged: WPF skips a PropertyChangedCallback when the incoming value
        // equals the current one, and every secret on a NEW connector arrives as "" over a
        // default of "" — so that subscription never happened and the field was inert.
        EventManager.RegisterClassHandler(typeof(PasswordBox), PasswordBox.PasswordChangedEvent, new RoutedEventHandler(OnPasswordChanged));
    }

    /// <summary>
    /// Default null, not "": it doubles as the "this box is bridged" marker. A PasswordBox
    /// nobody attached this to must never have its text written into the property, and the
    /// marker has to survive every value precedence — a binding declared inside a DataTemplate
    /// (the masked env-value row) is not a local value, so ReadLocalValue cannot see it.
    /// </summary>
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(PasswordBoxHelper),
        new FrameworkPropertyMetadata((string?)null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    private static readonly DependencyProperty IsUpdatingProperty = DependencyProperty.RegisterAttached(
        "IsUpdating", typeof(bool), typeof(PasswordBoxHelper), new PropertyMetadata(false));

    public static string GetBoundPassword(DependencyObject element) => (string?)element.GetValue(BoundPasswordProperty) ?? string.Empty;

    public static void SetBoundPassword(DependencyObject element, string value) => element.SetValue(BoundPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not PasswordBox box || (bool)box.GetValue(IsUpdatingProperty))
        {
            return;   // our own write-back, already in the box
        }
        box.SetValue(IsUpdatingProperty, true);
        box.Password = e.NewValue as string ?? string.Empty;
        box.SetValue(IsUpdatingProperty, false);
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        if ((bool)box.GetValue(IsUpdatingProperty) || box.GetValue(BoundPasswordProperty) is null)
        {
            return;   // our own write, or a PasswordBox this helper was never attached to
        }
        box.SetValue(IsUpdatingProperty, true);
        SetBoundPassword(box, box.Password);
        box.SetValue(IsUpdatingProperty, false);
    }
}

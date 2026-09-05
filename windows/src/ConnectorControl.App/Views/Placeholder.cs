using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace ConnectorControl.App.Views;

/// <summary>The Mac TextField prompt: grey placeholder text drawn over an empty TextBox.</summary>
public static class Placeholder
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Placeholder), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBox box || e.OldValue is not null)
        {
            return;   // hooked once per box
        }
        box.Loaded += (_, _) => Update(box);
        box.TextChanged += (_, _) => Update(box);
        box.SizeChanged += (_, _) => Update(box);
    }

    private static void Update(TextBox box)
    {
        var layer = AdornerLayer.GetAdornerLayer(box);
        if (layer is null)
        {
            return;
        }
        var existing = layer.GetAdorners(box)?.OfType<PlaceholderAdorner>().FirstOrDefault();
        var show = box.Text.Length == 0 && !string.IsNullOrEmpty(GetText(box));
        if (show && existing is null)
        {
            layer.Add(new PlaceholderAdorner(box, GetText(box)!));
        }
        else if (!show && existing is not null)
        {
            layer.Remove(existing);
        }
        else
        {
            existing?.InvalidateVisual();
        }
    }
}

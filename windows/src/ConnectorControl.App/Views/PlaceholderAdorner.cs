using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ConnectorControl.App.Views;

internal sealed class PlaceholderAdorner : Adorner
{
    private readonly string text;

    public PlaceholderAdorner(TextBox box, string text) : base(box)
    {
        this.text = text;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var box = (TextBox)AdornedElement;
        var typeface = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch);
        var brush = box.TryFindResource("TextFillColorTertiaryBrush") as Brush ?? Brushes.Gray;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, box.FontSize, brush, VisualTreeHelper.GetDpi(box).PixelsPerDip);
        // The Fluent TextBox template insets its content by roughly (10, 5); mirror it so the
        // placeholder sits where the caret will.
        var x = box.BorderThickness.Left + box.Padding.Left + 10;
        var y = Math.Max(0, (box.ActualHeight - formatted.Height) / 2);
        drawingContext.DrawText(formatted, new Point(x, y));
    }
}

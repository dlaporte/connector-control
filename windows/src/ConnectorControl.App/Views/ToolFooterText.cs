using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using ConnectorControl.Core.State;

namespace ConnectorControl.App.Views;

/// <summary>
/// Windows only: the Mac draws a ToolFooterLine as one attributed Text (ToolFooterLineView).
///
/// One line of the Settings Tools footer drawn into a TextBlock, so it wraps as a sentence: the link's title as a
/// hyperlink to the family's page, the command in MonoFont, the rest in the block's own face.
/// </summary>
public static class ToolFooterText
{
    public static readonly DependencyProperty LineProperty = DependencyProperty.RegisterAttached(
        "Line", typeof(ToolFooterLine), typeof(ToolFooterText), new PropertyMetadata(null, OnLineChanged));

    public static ToolFooterLine? GetLine(DependencyObject element) => (ToolFooterLine?)element.GetValue(LineProperty);

    public static void SetLine(DependencyObject element, ToolFooterLine? value) => element.SetValue(LineProperty, value);

    private static void OnLineChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock block)
        {
            return;
        }
        block.Inlines.Clear();
        if (e.NewValue is not ToolFooterLine line)
        {
            return;
        }
        foreach (var piece in line.Runs)
        {
            if (piece.IsSetApart && piece.Text == line.LinkTitle)
            {
                var link = new Hyperlink(new Run(piece.Text));
                link.Click += (_, _) => ExternalLink.Open(line.LinkUrl);
                block.Inlines.Add(link);
                continue;
            }
            var run = new Run(piece.ShownText);
            if (piece.IsSetApart)
            {
                run.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
            }
            block.Inlines.Add(run);
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using ConnectorControl.Core.State;

namespace ConnectorControl.App.Views;

/// <summary>
/// Windows only: the Mac counterpart is Sources/ConnectorControl/Text+Runs.swift, which draws TextRun with
/// SwiftUI's Text; its confirmation draws one as an attributed string (ConfirmationAccessory.swift).
///
/// A sentence with pieces set apart drawn into a TextBlock: words in the block's own face, set-apart pieces
/// in MonoFont, each as <see cref="TextRun.ShownText"/> spells it. <see cref="RunsProperty"/> binds it from
/// XAML; <see cref="Fill"/> draws it from code.
/// </summary>
public static class TextRuns
{
    public static readonly DependencyProperty RunsProperty = DependencyProperty.RegisterAttached(
        "Runs", typeof(IReadOnlyList<TextRun>), typeof(TextRuns), new PropertyMetadata(null, OnRunsChanged));

    public static IReadOnlyList<TextRun>? GetRuns(DependencyObject element) => (IReadOnlyList<TextRun>?)element.GetValue(RunsProperty);

    public static void SetRuns(DependencyObject element, IReadOnlyList<TextRun>? value) => element.SetValue(RunsProperty, value);

    public static void Fill(TextBlock block, IReadOnlyList<TextRun> runs)
    {
        block.Inlines.Clear();
        foreach (var piece in runs)
        {
            var run = new Run(piece.ShownText);
            if (piece.IsSetApart)
            {
                run.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
            }
            block.Inlines.Add(run);
        }
    }

    private static void OnRunsChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is TextBlock block)
        {
            Fill(block, (IReadOnlyList<TextRun>?)e.NewValue ?? []);
        }
    }
}

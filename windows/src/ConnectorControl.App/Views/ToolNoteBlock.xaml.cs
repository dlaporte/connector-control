using System.Windows;
using System.Windows.Controls;
using ConnectorControl.Core;

namespace ConnectorControl.App.Views;

/// <summary>
/// Spec 2026-09-05-tool-probe §3.4: the two-line tool note. Collapses itself while
/// <see cref="Note"/> is null, so callers bind it and forget it; Settings hides line 1 with
/// <see cref="ShowText"/> because its row already says "Not found".
/// </summary>
public partial class ToolNoteBlock : UserControl
{
    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
        nameof(Note), typeof(ToolNote), typeof(ToolNoteBlock), new PropertyMetadata(null, OnNoteChanged));

    public static readonly DependencyProperty ShowTextProperty = DependencyProperty.Register(
        nameof(ShowText), typeof(bool), typeof(ToolNoteBlock), new PropertyMetadata(true, OnShowTextChanged));

    public ToolNoteBlock()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
    }

    public ToolNote? Note
    {
        get => (ToolNote?)GetValue(NoteProperty);
        set => SetValue(NoteProperty, value);
    }

    public bool ShowText
    {
        get => (bool)GetValue(ShowTextProperty);
        set => SetValue(ShowTextProperty, value);
    }

    private static void OnNoteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var block = (ToolNoteBlock)d;
        block.Root.DataContext = e.NewValue;   // the inner bindings read the note; the control itself keeps the inherited context
        block.Visibility = e.NewValue is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnShowTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var block = (ToolNoteBlock)d;
        block.TextLine.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLink(object sender, RoutedEventArgs e)
    {
        if (Note is { } note)
        {
            ExternalLink.Open(note.LinkUrl);
        }
    }
}

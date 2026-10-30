using System.Windows;
using System.Windows.Controls;
using ConnectorControl.Core;

namespace ConnectorControl.App.Views;

/// <summary>
/// The two-line tool note: what is wrong, then the install line. Collapses itself while
/// <see cref="Note"/> is null, so callers bind it and forget it.
/// </summary>
public partial class ToolNoteBlock : UserControl
{
    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
        nameof(Note), typeof(ToolNote), typeof(ToolNoteBlock), new PropertyMetadata(null, OnNoteChanged));

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

    private static void OnNoteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var block = (ToolNoteBlock)d;
        block.Root.DataContext = e.NewValue;   // the inner bindings read the note; the control itself keeps the inherited context
        block.Visibility = e.NewValue is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnLink(object sender, RoutedEventArgs e)
    {
        if (Note is { } note)
        {
            ExternalLink.Open(note.LinkUrl);
        }
    }
}

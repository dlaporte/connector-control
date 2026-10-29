using System.Windows;
using System.Windows.Documents;
using ConnectorControl.Core.State;

namespace ConnectorControl.App.Views;

/// <summary>
/// The Mac NSAlert / confirmationDialog: message, informative text, a primary
/// button (accent, or red when destructive) and an optional Cancel. Native
/// MessageBox cannot carry the Mac's button labels, hence this window. A change
/// Claude asks for adds what would run and a closing line, as the Mac's alert
/// does in its accessory view.
/// </summary>
public partial class ConfirmDialog : DialogWindow
{
    public ConfirmDialog(string message, string? informativeText, string primaryTitle, string? cancelTitle, bool destructive,
                         bool cancelIsDefault = false)
    {
        InitializeComponent();
        MessageText.Text = message;
        InformativeText.Text = informativeText ?? string.Empty;
        InformativeText.Visibility = informativeText is null ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.Content = primaryTitle;
        CancelButton.Content = cancelTitle ?? string.Empty;
        CancelButton.Visibility = cancelTitle is null ? Visibility.Collapsed : Visibility.Visible;
        // The XAML default is AccentButtonStyle; destructive is the one runtime choice a Style can't make.
        if (destructive && TryFindResource("DestructiveButton") is Style style)
        {
            PrimaryButton.Style = style;
        }
        // Return presses the cancel button, and the primary answers only a click; the accent
        // goes with the default, and a destructive primary keeps its red. Escape stays on Cancel.
        if (cancelIsDefault)
        {
            PrimaryButton.IsDefault = false;
            CancelButton.IsDefault = true;
            if (!destructive)
            {
                PrimaryButton.ClearValue(StyleProperty);
            }
            CancelButton.SetResourceReference(StyleProperty, "AccentButtonStyle");
        }
    }

    /// <summary>How wide the code area's page is: far wider than any line of what would run, so none wraps.</summary>
    public const double UnwrappedWidth = 4000;

    /// <summary>The confirmation of a change Claude asks for: the question with every name in the monospaced
    /// face, what would run, the closing line, and Don’t Allow — the default, which Return and Escape press,
    /// as closing the window answers — before Allow.</summary>
    public ConfirmDialog(ConfirmationDialog question)
        : this(string.Empty, null, ConfirmationDialog.AllowButton, ConfirmationDialog.DontAllowButton, destructive: false, cancelIsDefault: true)
    {
        TextRuns.Fill(MessageText, question.Title);
        TextRuns.Fill(InformativeText, question.Message);
        InformativeText.Visibility = question.Message.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var lines = new Paragraph();
        for (var index = 0; index < question.Code.Count; index++)
        {
            if (index > 0)
            {
                lines.Inlines.Add(new LineBreak());
            }
            var line = new Run(question.Code[index]);
            // A theme without the colour leaves the line in the box's own; its "−" or "+" still says what it does.
            switch (question.CodeKinds[index])
            {
                case ConfirmationDialog.LineKind.Removed:
                    line.SetResourceReference(TextElement.ForegroundProperty, "SystemFillColorCriticalBrush");
                    break;
                case ConfirmationDialog.LineKind.Added:
                    line.SetResourceReference(TextElement.ForegroundProperty, "SystemFillColorSuccessBrush");
                    break;
            }
            lines.Inlines.Add(line);
        }
        CodeText.Document = new FlowDocument(lines) { PageWidth = UnwrappedWidth, PagePadding = new Thickness(0) };
        CodeText.Visibility = question.Code.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TextRuns.Fill(FooterText, question.Footer ?? []);
        FooterText.Visibility = question.Footer is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>True when the primary button was chosen.</summary>
    public bool Result { get; private set; }

    public static bool Show(Window? owner, string message, string? informativeText, string primaryTitle, string? cancelTitle, bool destructive,
                            bool cancelIsDefault = false)
    {
        var dialog = new ConfirmDialog(message, informativeText, primaryTitle, cancelTitle, destructive, cancelIsDefault);
        return Present(dialog, owner, () => dialog.Result);
    }

    /// <summary>Asks <paramref name="question"/> and says whether the answer was Allow.</summary>
    public static bool ShowChange(Window? owner, ConfirmationDialog question)
    {
        var dialog = new ConfirmDialog(question);
        return ClosedOnFailure(dialog, () => Present(dialog, owner, () => dialog.Result));
    }

    /// <summary>What <paramref name="show"/> answers; a failure closes <paramref name="dialog"/> before it goes on. A
    /// failure while the dialog is up leaves ShowDialog with the window still on screen, no longer modal, and
    /// the confirmer answers it Don’t Allow: closed, it leaves no Allow on screen that would count for nothing.</summary>
    internal static bool ClosedOnFailure(Window dialog, Func<bool> show)
    {
        try
        {
            return show();
        }
        catch (Exception)
        {
            if (dialog.IsVisible)
            {
                dialog.Close();
            }
            throw;
        }
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

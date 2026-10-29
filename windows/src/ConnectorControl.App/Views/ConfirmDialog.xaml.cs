using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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
        // Return presses the cancel button wherever the keyboard is, and the primary answers only a
        // click: a focused button takes Return itself, so the primary is told not to — Tab, or a press
        // dragged off it, can leave the keyboard there. The accent goes with the default, and a
        // destructive primary keeps its red. Escape stays on Cancel. The keyboard starts on the cancel
        // button, as it does on the Mac: the window gives it to the element its focus scope names when it
        // is first activated.
        if (cancelIsDefault)
        {
            PrimaryButton.IsDefault = false;
            KeyboardNavigation.SetAcceptsReturn(PrimaryButton, false);
            CancelButton.IsDefault = true;
            if (!destructive)
            {
                PrimaryButton.ClearValue(StyleProperty);
            }
            CancelButton.SetResourceReference(StyleProperty, "AccentButtonStyle");
            FocusManager.SetFocusedElement(this, CancelButton);
        }
    }

    /// <summary>How far the code area's page runs past its widest line, in ems: room for whatever the measure and
    /// the page's own layout round differently, so the widest line is never wrapped by a hair.</summary>
    private const double PageSlackEms = 4;

    /// <summary>The confirmation of a change Claude asks for: the question with every name in the monospaced
    /// face, what would run, the closing line, and Don’t Allow — the default, which Return presses wherever
    /// the keyboard is and Escape presses, as closing the window answers, and where the keyboard starts —
    /// before Allow, which answers only a click.</summary>
    public ConfirmDialog(ConfirmationDialog question)
        : this(string.Empty, null, ConfirmationDialog.AllowButton, ConfirmationDialog.DontAllowButton, destructive: false, cancelIsDefault: true)
    {
        // Platform idiom: the Mac's alert draws the title as plain text, NSAlert's message being a plain string;
        // this dialog's title is a text block of its own, so its names take the monospaced face as the message's do.
        TextRuns.Fill(MessageText, question.Title);
        TextRuns.Fill(InformativeText, question.Message);
        InformativeText.Visibility = question.Message.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CodeText.Document = CodeDocument(question);
        CodeText.Visibility = question.Code.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TextRuns.Fill(FooterText, question.Footer ?? []);
        FooterText.Visibility = question.Footer is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>What would run, a paragraph a line, as the Mac lays it out: each line is its own left-to-right
    /// paragraph, whatever the one before it holds, drawn in the order it is stored
    /// (<see cref="ConfirmationDialog.ShownCode"/>), and none is spaced from the next. Removed lines take the
    /// theme's critical colour and added ones its success colour; a theme without one leaves the line in the
    /// box's own, and its "−" or "+" still says what it does. The page is as wide as the widest line and some,
    /// so no line wraps, and only a line wider than the box gives it something to scroll to.</summary>
    private FlowDocument CodeDocument(ConfirmationDialog question)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = CodeText.FontFamily, FontSize = CodeText.FontSize };
        var shown = question.ShownCode;
        for (var index = 0; index < shown.Count; index++)
        {
            var line = new Run(shown[index]);
            switch (question.CodeKinds[index])
            {
                case ConfirmationDialog.LineKind.Removed:
                    line.SetResourceReference(TextElement.ForegroundProperty, "SystemFillColorCriticalBrush");
                    break;
                case ConfirmationDialog.LineKind.Added:
                    line.SetResourceReference(TextElement.ForegroundProperty, "SystemFillColorSuccessBrush");
                    break;
            }
            document.Blocks.Add(new Paragraph(line) { Margin = new Thickness(0) });
        }
        // Every line is within ControlTools.ShownLineLimit, as the tools refuse a question with a longer one
        // before asking it. So none reaches the 9,600 UTF-16 code units at which WPF breaks any line of text,
        // whatever the page's width, and FormattedText measures each whole. The page stays below the million
        // pixels a FlowDocument page may be, even at the widest glyphs; past them, setting it would throw and
        // the confirmer would answer Don’t Allow.
        document.PageWidth = WidestLine(shown) + PageSlackEms * CodeText.FontSize;
        return document;
    }

    /// <summary>How wide the widest of <paramref name="lines"/> is drawn in the code area's face, size, language
    /// and formatting mode, at this window's scale.</summary>
    private double WidestLine(IReadOnlyList<string> lines)
    {
        var typeface = new Typeface(CodeText.FontFamily, CodeText.FontStyle, CodeText.FontWeight, CodeText.FontStretch);
        var culture = CodeText.Language.GetSpecificCulture();
        var mode = TextOptions.GetTextFormattingMode(CodeText);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        return lines.Select(line => new FormattedText(line, culture, FlowDirection.LeftToRight, typeface, CodeText.FontSize, Brushes.Black,
                null, mode, pixelsPerDip).WidthIncludingTrailingWhitespace)
            .DefaultIfEmpty(0).Max();
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
    /// the confirmer answers it Don’t Allow: closed, it leaves no Allow on screen that would count for nothing.
    /// One before it shows closes it too, which is safe on a window never shown, and takes it off the
    /// application's list of windows, which it joined when it was built.</summary>
    internal static bool ClosedOnFailure(Window dialog, Func<bool> show)
    {
        try
        {
            return show();
        }
        catch (Exception)
        {
            dialog.Close();
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

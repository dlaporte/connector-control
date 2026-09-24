using System.Windows;
using System.Windows.Controls;

namespace ConnectorControl.App.Views;

/// <summary>
/// What every modal dialog here has in common — Confirm, NamePrompt, Update, Restore, and the
/// Collections window's Copy, Import, Publish and Review: a fixed, non-resizable, taskbar-hidden
/// modal that sizes to its content and shares the same background, plus the bits of code-behind
/// machinery each of them would otherwise write out itself.
/// </summary>
public abstract class DialogWindow : Window
{
    protected DialogWindow()
    {
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
    }

    /// <summary>
    /// The red line a dialog's verb answers on: the reason it could not act, shown, or null, which
    /// clears whatever an earlier answer left there. The models hand the message back rather than
    /// publishing a property for it, so neither does the line's visibility.
    /// </summary>
    protected static void ShowFailure(TextBlock line, string? failure)
    {
        line.Text = failure ?? string.Empty;
        line.Visibility = failure is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Shows <paramref name="dialog"/> via <see cref="WpfDialogs.Present"/> (owned and centered on
    /// <paramref name="owner"/>, or centered on screen and forced to the front when there is none)
    /// and returns whatever the caller's own result-reading callback says once it closes.
    /// </summary>
    protected static TResult Present<TResult>(DialogWindow dialog, Window? owner, Func<TResult> result)
    {
        WpfDialogs.Present(dialog, owner);
        return result();
    }
}

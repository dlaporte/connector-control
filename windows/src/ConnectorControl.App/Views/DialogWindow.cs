using System.Windows;

namespace ConnectorControl.App.Views;

/// <summary>
/// What ConfirmDialog, NamePromptDialog, UpdateDialog and RestoreDialog have in common: a fixed,
/// non-resizable, taskbar-hidden modal that sizes to its content and shares the same background,
/// plus the two bits of code-behind machinery every one of them otherwise wrote out itself.
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
    /// Wires a model event shaped like <c>CloseRequested</c> to this window's Close, marshalled
    /// onto the UI thread the way an event raised off a background continuation needs — the same
    /// <c>Dispatcher.BeginInvoke(new Action(Close))</c> every dialog with a model wrote by hand.
    /// </summary>
    protected void CloseWhenModelAsks(Action<Action> subscribe) =>
        subscribe(() => Dispatcher.BeginInvoke(new Action(Close)));

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

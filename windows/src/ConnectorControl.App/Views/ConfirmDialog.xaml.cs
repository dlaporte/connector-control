using System.Windows;

namespace ConnectorControl.App.Views;

/// <summary>
/// The Mac NSAlert / confirmationDialog: message, informative text, a primary
/// button (accent, or red when destructive) and an optional Cancel. Native
/// MessageBox cannot carry the Mac's button labels, hence this window.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string message, string? informativeText, string primaryTitle, string? cancelTitle, bool destructive)
    {
        InitializeComponent();
        MessageText.Text = message;
        InformativeText.Text = informativeText ?? string.Empty;
        InformativeText.Visibility = informativeText is null ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.Content = primaryTitle;
        CancelButton.Content = cancelTitle ?? string.Empty;
        CancelButton.Visibility = cancelTitle is null ? Visibility.Collapsed : Visibility.Visible;
        if (TryFindResource(destructive ? "DestructiveButton" : "AccentButtonStyle") is Style style)
        {
            PrimaryButton.Style = style;
        }
    }

    /// <summary>True when the primary button was chosen.</summary>
    public bool Result { get; private set; }

    public static bool Show(Window? owner, string message, string? informativeText, string primaryTitle, string? cancelTitle, bool destructive)
    {
        var dialog = new ConfirmDialog(message, informativeText, primaryTitle, cancelTitle, destructive);
        WpfDialogs.Present(dialog, owner);
        return dialog.Result;
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

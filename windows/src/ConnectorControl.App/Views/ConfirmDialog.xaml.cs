using System.Windows;

namespace ConnectorControl.App.Views;

/// <summary>
/// The Mac NSAlert / confirmationDialog: message, informative text, a primary
/// button (accent, or red when destructive) and an optional Cancel. Native
/// MessageBox cannot carry the Mac's button labels, hence this window.
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

    /// <summary>True when the primary button was chosen.</summary>
    public bool Result { get; private set; }

    public static bool Show(Window? owner, string message, string? informativeText, string primaryTitle, string? cancelTitle, bool destructive,
                            bool cancelIsDefault = false)
    {
        var dialog = new ConfirmDialog(message, informativeText, primaryTitle, cancelTitle, destructive, cancelIsDefault);
        return Present(dialog, owner, () => dialog.Result);
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

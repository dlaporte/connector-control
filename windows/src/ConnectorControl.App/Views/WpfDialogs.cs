using System.Windows;
using ConnectorControl.Core.State;

namespace ConnectorControl.App.Views;

/// <summary>
/// IDialogs on WPF. Owned by a window (editor, settings, restore) the dialogs
/// center on it; tray-initiated dialogs have no owner and are centered on
/// screen and forced to the front, like the Mac's NSApp.activate before NSAlert.
/// </summary>
public sealed class WpfDialogs : IDialogs
{
    private readonly Func<Window?> owner;

    public WpfDialogs(Func<Window?> owner)
    {
        this.owner = owner;
    }

    /// <summary>
    /// The window to centre on: the one that asked, or — for the tray-initiated
    /// instance that passes none — whichever of our windows is active, so
    /// Settings ▸ Check for Updates… centres on Settings and not on the screen.
    /// Null means there is nothing of ours on screen: centre and force to front.
    /// </summary>
    internal Window? ResolveOwner() =>
        owner() ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible && w.IsActive);

    public bool Confirm(string message, string? informativeText, string primaryTitle, string cancelTitle = "Cancel", bool destructive = false) =>
        ConfirmDialog.Show(ResolveOwner(), message, informativeText, primaryTitle, cancelTitle, destructive);

    public string? PromptForName(string title, string initial) => NamePromptDialog.Show(ResolveOwner(), title, initial);

    public void Inform(string message, string? informativeText) =>
        ConfirmDialog.Show(ResolveOwner(), message, informativeText, "OK", null, destructive: false);

    public bool OfferUpdate(string newVersion, string currentVersion, string? notesMarkdown) =>
        UpdateDialog.Show(ResolveOwner(), newVersion, currentVersion, notesMarkdown);

    internal static void Present(Window dialog, Window? owner)
    {
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
        }
        dialog.Loaded += (_, _) => dialog.Activate();
        dialog.ShowDialog();
    }
}

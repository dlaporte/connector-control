using System.Windows;
using ConnectorControl.Core.State;

namespace ConnectorControl.App.Views;

/// <summary>
/// IDialogs on WPF. Given a window — the editor's, whose dialogs center on it — or given none,
/// which is App's instance, shared by AppState, the update coordinator and the Collections window's
/// model: that one centers on whichever of our windows is active, and with none of ours up it
/// centers on screen and is forced to the front, like the Mac's NSApp.activate before NSAlert.
/// <see cref="Present"/> is also how every dialog here is shown, Settings' Restore and the
/// Collections window's five among them.
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
    /// Settings ▸ Check for Updates centres on Settings and not on the screen.
    /// Null means there is nothing of ours on screen: centre and force to front.
    /// Never the flyout: it hides itself on Deactivated, which is exactly what
    /// showing a modal over it does, so Quit / Restart Required / the collection
    /// prompts would end up owned by a hidden window instead.
    /// </summary>
    internal Window? ResolveOwner() =>
        owner() ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible && w.IsActive && w is not FlyoutWindow);

    public bool Confirm(string message, string? informativeText, string primaryTitle, string cancelTitle, bool destructive,
                        bool cancelIsDefault) =>
        ConfirmDialog.Show(ResolveOwner(), message, informativeText, primaryTitle, cancelTitle, destructive, cancelIsDefault);

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

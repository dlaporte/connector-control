using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ConnectorControl.App.Views;

/// <summary>
/// Where the keyboard starts in a window or dialog that has just opened: the first text field that
/// can take it, which is where AppKit puts it when a Mac window or sheet first becomes key. WPF
/// otherwise starts nowhere, and the first key typed into a fresh editor would go nowhere.
/// </summary>
internal static class InitialFocus
{
    /// <summary>
    /// On the window's first activation, and only when nothing inside it has claimed the keyboard
    /// already. Activation rather than load: focusing a window that is not active would activate
    /// it. WPF raises Activated before Loaded, so a dialog that focuses its own field on load, as
    /// the name prompt does, focuses it after this and wins; it is the same field.
    /// </summary>
    public static void OnFirstActivation(Window window)
    {
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            window.Activated -= handler;
            if (!window.IsKeyboardFocusWithin && FirstField(window) is { } field)
            {
                field.Focus();
            }
        };
        window.Activated += handler;
    }

    /// <summary>
    /// The first editable text or password box in the tree, in layout order, that is shown and
    /// enabled; a read-only box, such as a preview, is not a field. The window's own visibility is
    /// not asked: the App test searches a window laid out before it is shown, while it is still
    /// Collapsed. At the one call above, on activation, the window is always visible.
    /// </summary>
    internal static Control? FirstField(DependencyObject root)
    {
        if ((root is UIElement { Visibility: not Visibility.Visible } and not Window) || root is UIElement { IsEnabled: false })
        {
            return null;
        }
        if (root is TextBox { IsReadOnly: false, Focusable: true } or PasswordBox { Focusable: true })
        {
            return (Control)root;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FirstField(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }
        return null;
    }
}

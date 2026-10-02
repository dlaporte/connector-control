using System.Windows;
using System.Windows.Media;

namespace ConnectorControl.App.Views;

/// <summary>A depth-first walk of the visual tree, and the walk up it, shared by the code that
/// needs them (EditorWindow's env-row focus, the Collections window's rows and their tick slots)
/// and the tests that reach into a rendered window's generated containers the same way.</summary>
internal static class VisualTree
{
    public static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }
            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }
        return null;
    }

    /// <summary>The nearest element above <paramref name="start"/> of type <typeparamref name="T"/>, not counting itself.</summary>
    public static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var node = VisualTreeHelper.GetParent(start); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is T found)
            {
                return found;
            }
        }
        return null;
    }
}

using System.Windows;
using System.Windows.Media;

namespace ConnectorControl.App.Views;

/// <summary>A depth-first walk of the visual tree, shared by the code that needs it
/// (EditorWindow's env-row focus) and the tests that reach into a rendered window's
/// generated containers the same way.</summary>
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
}

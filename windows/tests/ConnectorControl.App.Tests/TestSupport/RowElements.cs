using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ConnectorControl.App.Tests.TestSupport;

/// <summary>
/// A named element inside a generated row. A DataTemplate's names live in the template's own
/// namescope, out of the window's reach, but every element still carries its Name — so a walk of
/// the row's own visual tree finds it. EditorWindowTests has the same walk file-private; this is
/// the shared copy, for the window tests that reach into rows from more than one file.
/// </summary>
internal static class RowElements
{
    public static T Find<T>(ItemsControl list, object row, string name)
        where T : FrameworkElement
    {
        list.UpdateLayout();
        var container = list.ItemContainerGenerator.ContainerFromItem(row);
        Assert.NotNull(container);
        var found = Named<T>(container, name);
        Assert.NotNull(found);
        return found;
    }

    private static T? Named<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name)
            {
                return match;
            }
            if (Named<T>(child, name) is { } deeper)
            {
                return deeper;
            }
        }
        return null;
    }
}

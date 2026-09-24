using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ConnectorControl.App.Views;

/// <summary>
/// The context menus the windows and the tray build in code, and their entries, made one way so
/// an entry's click, name and placement cannot drift between copies.
/// </summary>
internal static class Menus
{
    /// <summary>A menu that opens against <paramref name="target"/> and closes on the next click elsewhere.</summary>
    public static ContextMenu Anchored(UIElement target, PlacementMode placement) =>
        new() { PlacementTarget = target, Placement = placement, StaysOpen = false };

    /// <summary>One entry: its words, and what a click on it does.</summary>
    public static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// A two-line entry: the name, and under it the subtitle that tells it apart, in
    /// <paramref name="subtitleStyle"/>. A header built from elements gives the item nothing of its
    /// own to announce, so the item is given the name and the subtitle as its help text.
    /// </summary>
    public static MenuItem Item(string header, string subtitle, Style subtitleStyle, Action action)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = header });
        panel.Children.Add(new TextBlock { Text = subtitle, Style = subtitleStyle });
        var item = new MenuItem { Header = panel };
        AutomationProperties.SetName(item, header);
        AutomationProperties.SetHelpText(item, subtitle);
        item.Click += (_, _) => action();
        return item;
    }
}

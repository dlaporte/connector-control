using System.Windows;

namespace ConnectorControl.App.Views;

internal static class WindowExtensions
{
    /// <summary>
    /// Wires a model event shaped like <c>CloseRequested</c> to <paramref name="window"/>'s Close,
    /// marshalled onto the UI thread the way an event raised off a background continuation needs.
    /// For the editor and every dialog with a model, which would otherwise each write
    /// <c>Dispatcher.BeginInvoke(new Action(Close))</c> by hand.
    /// </summary>
    public static void CloseWhenAsked(this Window window, Action<Action> subscribe) =>
        subscribe(() => window.Dispatcher.BeginInvoke(new Action(window.Close)));
}

using System.Runtime.InteropServices;
using System.Security;
using ConnectorControl.Core.Services;
using Microsoft.Toolkit.Uwp.Notifications;

namespace ConnectorControl.App.Services;

/// <summary>
/// UNUserNotificationCenter's role (spec §6.4): toasts via the Community
/// Toolkit's unpackaged-app support, with a Restart Claude button on the
/// restart category routed back through <c>marshal</c> (the UI thread).
/// Notifications are best effort: if the toast platform is unavailable the
/// app keeps working silently, like the Mac app without permission.
/// </summary>
public sealed class ToastNotifier : INotifier, IDisposable
{
    private const string ActionKey = "action";
    private readonly Action<Action> marshal;
    private bool hooked;

    public ToastNotifier(Action<Action> marshal)
    {
        this.marshal = marshal;
        try
        {
            ToastNotificationManagerCompat.OnActivated += OnActivated;
            hooked = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException
            or IOException or SecurityException)
        {
            // Subscribing registers a COM server under HKCU\Software\Classes for this
            // unpackaged app, so the registry's failures count too. Activation is then
            // unavailable; Notify still tries to show toasts. Never block startup.
            hooked = false;
        }
    }

    public event Action? RestartActionActivated;

    public void Notify(string title, string body, string? category = null)
    {
        try
        {
            Build(title, body, category).Show();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException
            or IOException or SecurityException)
        {
            // toast platform unavailable: stay silent
        }
    }

    /// <summary>
    /// The toast itself: title, body, and — for the restart category only — the
    /// Restart Claude button carrying the action this class routes back
    /// (spec §6.4). Separate from Show() so the content is testable.
    /// </summary>
    internal static ToastContentBuilder Build(string title, string body, string? category)
    {
        var builder = new ToastContentBuilder().AddText(title).AddText(body);
        if (category == Notifications.RestartCategory)
        {
            builder.AddButton(new ToastButton()
                .SetContent(Notifications.RestartButton)
                .AddArgument(ActionKey, Notifications.RestartAction));
        }
        return builder;
    }

    internal static bool IsRestartActivation(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return false;
        }
        var args = ToastArguments.Parse(argument);
        return args.TryGetValue(ActionKey, out var action) && action == Notifications.RestartAction;
    }

    /// <summary>Routes a toast activation argument string; internal so the tests can raise it.</summary>
    internal void HandleActivation(string argument)
    {
        if (IsRestartActivation(argument))
        {
            marshal(() => RestartActionActivated?.Invoke());
        }
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e) => HandleActivation(e.Argument);

    public void Dispose()
    {
        if (hooked)
        {
            ToastNotificationManagerCompat.OnActivated -= OnActivated;
            hooked = false;
        }
    }
}

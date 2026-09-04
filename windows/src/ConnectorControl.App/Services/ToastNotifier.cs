using System.Runtime.InteropServices;
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
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            hooked = false;   // activation unavailable; Notify still tries to show toasts
        }
    }

    public event Action? RestartActionActivated;

    public void Notify(string title, string body, string? category = null)
    {
        try
        {
            var builder = new ToastContentBuilder().AddText(title).AddText(body);
            if (category == Notifications.RestartCategory)
            {
                builder.AddButton(new ToastButton()
                    .SetContent(Notifications.RestartButton)
                    .AddArgument(ActionKey, Notifications.RestartAction));
            }
            builder.Show();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // toast platform unavailable: stay silent
        }
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

    /// <summary>Routes a toast activation argument string; public for tests.</summary>
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

using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.State;

/// <summary>Spec §7.1: one toast on first launch, remembered in settings.</summary>
public static class FirstRunTip
{
    public const string Body = "Connector Control lives in the system tray. Drag its icon out of the overflow (^) to keep it visible.";

    public static bool ShowIfNeeded(ISettings settings, INotifier notifier)
    {
        if (settings.TrayTipShown)
        {
            return false;
        }
        notifier.Notify(Notifications.Title, Body);
        settings.TrayTipShown = true;
        return true;
    }
}

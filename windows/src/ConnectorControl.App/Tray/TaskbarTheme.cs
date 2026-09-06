using System.Security;
using Microsoft.Win32;

namespace ConnectorControl.App.Tray;

/// <summary>
/// Spec §7.1: the tray icon color follows the TASKBAR theme (SystemUsesLightTheme),
/// which is independent of the app theme (AppsUseLightTheme). Missing value = dark
/// taskbar, the Windows 10/11 default.
/// </summary>
public static class TaskbarTheme
{
    public const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public const string ValueName = "SystemUsesLightTheme";

    public static bool IsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return IsLight(key?.GetValue(ValueName));
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsLight(object? registryValue) => registryValue is int value && value != 0;
}

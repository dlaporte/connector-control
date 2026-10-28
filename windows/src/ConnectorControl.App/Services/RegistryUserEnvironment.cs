using System.Runtime.InteropServices;
using ConnectorControl.Core.Transport;
using Microsoft.Win32;

namespace ConnectorControl.App.Services;

/// <summary>
/// The user PATH in HKEY_CURRENT_USER\Environment. A change is broadcast as WM_SETTINGCHANGE "Environment",
/// so Explorer — and every terminal started from it afterwards — sees it without signing out.
/// </summary>
internal sealed class RegistryUserEnvironment : IUserEnvironment
{
    private const string EnvironmentKey = "Environment";
    private const string PathValue = "Path";
    private const nint HwndBroadcast = 0xffff;
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint ReplyTimeoutMs = 5000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, string lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

    public string? ReadPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(EnvironmentKey);
        return key?.GetValue(PathValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void WritePath(string value)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(EnvironmentKey, writable: true))
        {
            // The kind it already is — a PATH holding %VARIABLES% is REG_EXPAND_SZ — or the kind Windows gives a new one.
            var kind = key.GetValueNames().Contains(PathValue, StringComparer.OrdinalIgnoreCase)
                ? key.GetValueKind(PathValue) : RegistryValueKind.ExpandString;
            if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            {
                kind = RegistryValueKind.ExpandString;
            }
            if (value.Length == 0)
            {
                key.DeleteValue(PathValue, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(PathValue, value, kind);
            }
        }
        _ = SendMessageTimeout(HwndBroadcast, WmSettingChange, 0, "Environment", SmtoAbortIfHung, ReplyTimeoutMs, out _);
    }
}

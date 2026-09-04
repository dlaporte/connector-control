using System.Security;
using ConnectorControl.Core.Services;
using Microsoft.Win32;

namespace ConnectorControl.App.Services;

/// <summary>
/// SMAppService's role (spec §6.6): a value under HKCU\...\Run pointing at
/// this executable. Velopack's launcher path is stable across updates.
/// Windows keeps a second, authoritative opinion under StartupApproved\Run:
/// disabling an entry in Settings ▸ Apps ▸ Startup or Task Manager ▸ Startup
/// leaves the Run value alone and writes a "disabled" marker there instead, so
/// both must be read and written together or our toggle disagrees with Windows.
/// </summary>
public sealed class RegistryAutostart : IAutostart
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string DefaultValueName = "Connector Control";

    private readonly string valueName;

    public RegistryAutostart(string? valueName = null, string? executablePath = null)
    {
        this.valueName = valueName ?? DefaultValueName;
        var exe = executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("The executable path is unknown.");
        Command = "\"" + exe + "\"";
    }

    /// <summary>The registry value written when enabled: the quoted executable path.</summary>
    public string Command { get; }

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                if (run?.GetValue(valueName) is not string value || value.Length == 0)
                {
                    return false;
                }
                using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, writable: false);
                return approved?.GetValue(valueName) is not byte[] { Length: > 0 } marker || !IsDisabledMarker(marker[0]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return false;   // unreadable: report off rather than promise a startup we cannot verify
            }
        }
    }

    /// <summary>
    /// The first byte of a StartupApproved value says whether Windows will run the
    /// entry: 0x02 (enabled) and 0x06 (enabled again after being disabled) are the
    /// only "yes" values; 0x03 is what Task Manager writes when the user turns an
    /// entry off. Anything else is treated as disabled.
    /// </summary>
    internal static bool IsDisabledMarker(byte first) => first is not (0x02 or 0x06);

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("The Run key could not be opened.");
            if (enabled)
            {
                run.SetValue(valueName, Command, RegistryValueKind.String);
            }
            else
            {
                run.DeleteValue(valueName, throwOnMissingValue: false);
            }
            // Either way the veto goes: on enable so Windows honours the Run value,
            // on disable so a later enable does not inherit a stale marker.
            using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, writable: true);
            approved?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }
}

using System.Security;
using ConnectorControl.Core.Services;
using Microsoft.Win32;

namespace ConnectorControl.App.Services;

/// <summary>
/// SMAppService's role (spec §6.6): a value under HKCU\...\Run pointing at
/// this executable. Velopack's stub launcher path is stable across updates.
/// </summary>
public sealed class RegistryAutostart : IAutostart
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
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
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(valueName) is string value && value.Length > 0;
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("The Run key could not be opened.");
            if (enabled)
            {
                key.SetValue(valueName, Command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }
}

using ConnectorControl.App.Services;
using Microsoft.Win32;

namespace ConnectorControl.App.Tests;

public class RegistryAutostartTests : IDisposable
{
    private readonly string valueName = "ConnectorControl.Test." + Guid.NewGuid().ToString("N");
    private readonly string exe = Path.Combine(Path.GetTempPath(), "cc-autostart", "ConnectorControl.exe");

    public void Dispose()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryAutostart.RunKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    [Fact]
    public void DisabledByDefault()
    {
        Assert.False(new RegistryAutostart(valueName, exe).IsEnabled);
    }

    [Fact]
    public void EnableWritesQuotedCommandAndDisableRemovesIt()
    {
        var autostart = new RegistryAutostart(valueName, exe);
        autostart.SetEnabled(true);
        Assert.True(autostart.IsEnabled);
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryAutostart.RunKeyPath))
        {
            Assert.Equal("\"" + exe + "\"", key!.GetValue(valueName));
        }
        Assert.True(new RegistryAutostart(valueName, exe).IsEnabled);   // read fresh by a new instance
        autostart.SetEnabled(false);
        Assert.False(autostart.IsEnabled);
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryAutostart.RunKeyPath))
        {
            Assert.Null(key!.GetValue(valueName));
        }
    }

    [Fact]
    public void DisablingWhenAlreadyDisabledIsFine()
    {
        var autostart = new RegistryAutostart(valueName, exe);
        autostart.SetEnabled(false);
        Assert.False(autostart.IsEnabled);
    }

    [Fact]
    public void DefaultsPointAtThisExecutable()
    {
        var autostart = new RegistryAutostart();
        Assert.Equal("\"" + Environment.ProcessPath + "\"", autostart.Command);
    }
}

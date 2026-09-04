using ConnectorControl.App.Services;
using Microsoft.Win32;

namespace ConnectorControl.App.Tests;

public class RegistryAutostartTests : IDisposable
{
    private readonly string valueName = "ConnectorControl.Test." + Guid.NewGuid().ToString("N");
    private readonly string exe = Path.Combine(Path.GetTempPath(), "cc-autostart", "ConnectorControl.exe");

    public void Dispose()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RegistryAutostart.RunKeyPath, writable: true))
        {
            run?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        using var approved = Registry.CurrentUser.OpenSubKey(RegistryAutostart.StartupApprovedKeyPath, writable: true);
        approved?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    /// <summary>Writes what Task Manager ▸ Startup writes: 12 bytes whose first says enabled or disabled.</summary>
    private void WriteApproval(byte first)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryAutostart.StartupApprovedKeyPath, writable: true)!;
        var value = new byte[12];
        value[0] = first;
        key.SetValue(valueName, value, RegistryValueKind.Binary);
    }

    private byte[]? ReadApproval()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryAutostart.StartupApprovedKeyPath);
        return key?.GetValue(valueName) as byte[];
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

    [Theory]
    [InlineData((byte)0x02, false)]   // enabled
    [InlineData((byte)0x06, false)]   // enabled again after having been disabled
    [InlineData((byte)0x03, true)]    // what Task Manager writes when the user turns it off
    [InlineData((byte)0x01, true)]
    [InlineData((byte)0x00, true)]
    public void DisabledMarkerIsRecognized(byte first, bool disabled)
    {
        Assert.Equal(disabled, RegistryAutostart.IsDisabledMarker(first));
    }

    [Fact]
    public void DisabledInWindowsSettingsReadsAsOffEvenWithTheRunValuePresent()
    {
        var autostart = new RegistryAutostart(valueName, exe);
        autostart.SetEnabled(true);
        Assert.True(autostart.IsEnabled);
        WriteApproval(0x03);   // the user turned it off in Task Manager ▸ Startup
        Assert.False(autostart.IsEnabled);
        using var run = Registry.CurrentUser.OpenSubKey(RegistryAutostart.RunKeyPath);
        Assert.NotNull(run!.GetValue(valueName));
    }

    [Fact]
    public void EnablingClearsTheWindowsVeto()
    {
        var autostart = new RegistryAutostart(valueName, exe);
        WriteApproval(0x03);
        autostart.SetEnabled(true);
        Assert.Null(ReadApproval());
        Assert.True(autostart.IsEnabled);
    }

    [Fact]
    public void AnEnabledApprovalIsNotAVeto()
    {
        var autostart = new RegistryAutostart(valueName, exe);
        autostart.SetEnabled(true);
        WriteApproval(0x02);
        Assert.True(autostart.IsEnabled);
    }

    [Fact]
    public void DisablingRemovesTheRunValueAndTheApproval()
    {
        var autostart = new RegistryAutostart(valueName, exe);
        autostart.SetEnabled(true);
        WriteApproval(0x02);
        autostart.SetEnabled(false);
        Assert.False(autostart.IsEnabled);
        using var run = Registry.CurrentUser.OpenSubKey(RegistryAutostart.RunKeyPath);
        Assert.Null(run!.GetValue(valueName));
        Assert.Null(ReadApproval());
    }

    [Fact]
    public void DefaultsPointAtThisExecutable()
    {
        var autostart = new RegistryAutostart();
        Assert.Equal("\"" + Environment.ProcessPath + "\"", autostart.Command);
    }
}

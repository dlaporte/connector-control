using System.Text;
using ConnectorControl.Core.Services;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly TempDir dir = new("settings");
    private string Path => dir.File("settings.json");

    public void Dispose() => dir.Dispose();

    [Fact]
    public void DefaultsWhenFileIsMissing()
    {
        var s = new SettingsStore(Path);
        Assert.Null(s.MasterStoreDir);
        Assert.Null(s.ClaudeConfigPath);
        Assert.Null(s.ClaudeLaunchTarget);
        Assert.Equal(20, s.BackupKeepCount);
        Assert.True(s.NotifyExternalChanges);
        Assert.True(s.ConfirmBeforeRestart);
        Assert.True(s.ConfirmBeforeQuit);
        Assert.Null(s.LastApplyDate);
        Assert.False(s.AclSweepDone);
        Assert.True(s.AutoUpdate);
        Assert.False(s.TrayTipShown);
        Assert.False(File.Exists(Path));   // reading never creates the file
    }

    [Fact]
    public void SettersPersistImmediatelyAndReloadRoundTrips()
    {
        var s = new SettingsStore(Path);
        s.MasterStoreDir = @"D:\Dropbox\cc";
        s.BackupKeepCount = 7;
        s.NotifyExternalChanges = false;
        s.LastApplyDate = new DateTime(2026, 9, 3, 12, 34, 56, DateTimeKind.Utc);
        s.AclSweepDone = true;
        Assert.True(File.Exists(Path));

        var again = new SettingsStore(Path);
        Assert.Equal(@"D:\Dropbox\cc", again.MasterStoreDir);
        Assert.Equal(7, again.BackupKeepCount);
        Assert.False(again.NotifyExternalChanges);
        Assert.Equal(new DateTime(2026, 9, 3, 12, 34, 56, DateTimeKind.Utc), again.LastApplyDate);
        Assert.Equal(DateTimeKind.Utc, again.LastApplyDate!.Value.Kind);
        Assert.True(again.AclSweepDone);
        Assert.True(again.ConfirmBeforeQuit);   // untouched keys keep their defaults
    }

    [Fact]
    public void SettingNullRemovesTheKey()
    {
        var s = new SettingsStore(Path);
        s.MasterStoreDir = "x";
        s.MasterStoreDir = null;
        Assert.DoesNotContain("masterStoreDir", File.ReadAllText(Path));
        Assert.Null(new SettingsStore(Path).MasterStoreDir);
    }

    [Fact]
    public void UnknownKeysAndWrongTypesAreTolerated()
    {
        File.WriteAllText(Path, "{\"future\": {\"x\": 1}, \"backupKeepCount\": \"lots\", \"notifyExternalChanges\": 1, \"lastApplyDate\": \"not a date\"}");
        var s = new SettingsStore(Path);
        Assert.Equal(20, s.BackupKeepCount);          // wrong type → default
        Assert.True(s.NotifyExternalChanges);
        Assert.Null(s.LastApplyDate);
        s.TrayTipShown = true;                         // a write preserves the unknown key
        var text = File.ReadAllText(Path);
        Assert.Contains("\"future\"", text);
        Assert.Contains("\"trayTipShown\" : true", text);
    }

    [Fact]
    public void CorruptFileBehavesLikeMissingUntilOverwritten()
    {
        File.WriteAllText(Path, "{not json");
        var s = new SettingsStore(Path);
        Assert.Equal(20, s.BackupKeepCount);
        s.BackupKeepCount = 5;
        Assert.Equal(5, new SettingsStore(Path).BackupKeepCount);
    }

    [Fact]
    public void ReloadPicksUpExternalEdits()
    {
        var s = new SettingsStore(Path);
        s.BackupKeepCount = 9;
        File.WriteAllText(Path, "{\"backupKeepCount\": 33}");
        Assert.Equal(9, s.BackupKeepCount);   // cached
        s.Reload();
        Assert.Equal(33, s.BackupKeepCount);
    }

    [Fact]
    public void FileIsWrittenInAppleEncoderFormat()
    {
        var s = new SettingsStore(Path);
        s.ConfirmBeforeQuit = false;
        Assert.Equal("{\n  \"confirmBeforeQuit\" : false\n}", File.ReadAllText(Path, Encoding.UTF8));
    }

    [Fact]
    public void FileIsOwnerOnlyOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }
        var s = new SettingsStore(Path);
        s.TrayTipShown = true;
        Assert.True(OwnerOnlyAcl.IsOwnerOnly(Path));
    }
}

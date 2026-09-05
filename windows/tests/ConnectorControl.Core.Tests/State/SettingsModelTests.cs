using ConnectorControl.Core.Services;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

public class SettingsModelTests
{
    private sealed class Rig : IDisposable
    {
        public AppStateHarness H { get; } = new();
        public AppState State { get; }
        public FakeAutostart Autostart { get; } = new();
        public FakeClaudeInstall Install { get; } = new();
        public FakeUpdater Updater { get; } = new();
        public UpdateCoordinator Updates { get; }
        public SettingsModel Model { get; }

        public Rig()
        {
            State = H.Create();
            Updates = new UpdateCoordinator(Updater, H.Settings, H.Notifier, H.Dialogs, AppHost.Inline());
            Model = new SettingsModel(State, H.Settings, Autostart, Install, Updater, Updates);
        }

        public void Dispose()
        {
            Updates.Dispose();
            State.Dispose();
            H.Dispose();
        }
    }

    [Fact]
    public void LaunchAtStartupTogglesAutostart()
    {
        using var rig = new Rig();
        Assert.False(rig.Model.LaunchAtStartup);
        rig.Model.LaunchAtStartup = true;
        Assert.True(rig.Autostart.Enabled);
        Assert.Null(rig.Model.LoginItemNote);
        rig.Model.LaunchAtStartup = false;
        Assert.False(rig.Autostart.Enabled);
        Assert.Equal(2, rig.Autostart.SetCalls);
    }

    [Fact]
    public void LaunchAtStartupDoesNothingWhenTheOsAlreadyAgrees()
    {
        using var rig = new Rig();
        rig.Autostart.Enabled = true;   // enabled behind our back (Windows Settings)
        rig.Model.Refresh();
        Assert.True(rig.Model.LaunchAtStartup);
        rig.Model.LaunchAtStartup = true;
        Assert.Equal(0, rig.Autostart.SetCalls);
    }

    [Fact]
    public void LaunchAtStartupFailureRevertsAndNotes()
    {
        using var rig = new Rig();
        rig.Autostart.FailWith = "Access is denied.";
        rig.Model.LaunchAtStartup = true;
        Assert.False(rig.Model.LaunchAtStartup);
        Assert.Equal("Couldn't update login item: Access is denied.", rig.Model.LoginItemNote);
        rig.Autostart.FailWith = null;
        rig.Model.LaunchAtStartup = true;
        Assert.Null(rig.Model.LoginItemNote);
    }

    [Fact]
    public void ConfirmAndNotifyTogglesPersistToSettings()
    {
        using var rig = new Rig();
        rig.Model.ConfirmBeforeRestart = false;
        rig.Model.ConfirmBeforeQuit = false;
        rig.Model.NotifyExternalChanges = false;
        rig.Model.AutoUpdate = false;
        Assert.False(rig.H.Settings.ConfirmBeforeRestart);
        Assert.False(rig.H.Settings.ConfirmBeforeQuit);
        Assert.False(rig.H.Settings.NotifyExternalChanges);
        Assert.False(rig.H.Settings.AutoUpdate);
    }

    [Fact]
    public void UpdatesAreDisabledForADevelopmentBuild()
    {
        using var rig = new Rig();
        rig.Updater.IsAvailable = false;
        rig.Updater.VersionDisplay = "development build";
        rig.Model.Refresh();
        Assert.False(rig.Model.UpdatesEnabled);
        Assert.Equal("Version development build", rig.Model.VersionText);
    }

    [Fact]
    public async Task CheckForUpdatesRunsAnInteractiveCheck()
    {
        using var rig = new Rig();
        rig.Updater.VersionDisplay = "1.3.0";
        Assert.Equal("Version 1.3.0", rig.Model.VersionText);
        await rig.Model.CheckForUpdatesAsync();
        Assert.Equal(1, rig.Updater.Checks);
        Assert.Equal("You're up to date.", rig.H.Dialogs.Informs[0].Message);
    }

    [Fact]
    public void BackupKeepCountClampsAndRebuildsTheService()
    {
        using var rig = new Rig();
        Assert.Equal("Keep 20 backups of each file", rig.Model.KeepCountLabel);
        rig.Model.BackupKeepCount = 500;
        Assert.Equal(100, rig.Model.BackupKeepCount);
        Assert.Equal(100, rig.H.Settings.BackupKeepCount);
        Assert.Equal(100, rig.State.Service.Backups.KeepCount);
        rig.Model.BackupKeepCount = 1;
        Assert.Equal(5, rig.Model.BackupKeepCount);
        rig.Model.DecrementKeepCount();
        Assert.Equal(5, rig.Model.BackupKeepCount);
        rig.Model.IncrementKeepCount();
        Assert.Equal(6, rig.Model.BackupKeepCount);
        Assert.Equal("Keep 6 backups of each file", rig.Model.KeepCountLabel);
    }

    [Fact]
    public void StoreLocationFollowsRepointing()
    {
        using var rig = new Rig();
        Assert.Equal(rig.H.StoreDir, rig.Model.StoreDirPath);
        Assert.False(rig.Model.CanUseDefaultStore);
        var synced = rig.H.Dir.File("synced");
        rig.Model.ChooseStoreDir(synced);
        Assert.Equal(synced, rig.Model.StoreDirPath);
        Assert.True(rig.Model.CanUseDefaultStore);
        Assert.Equal(rig.H.BackupsDir, rig.Model.BackupsDir);
        rig.Model.UseDefaultStoreDir();
        Assert.Equal(rig.H.StoreDir, rig.Model.StoreDirPath);
        Assert.False(rig.Model.CanUseDefaultStore);
    }

    [Theory]
    [InlineData(ClaudeInstallKind.Msix, "MSIX package")]
    [InlineData(ClaudeInstallKind.Legacy, "Legacy installer")]
    [InlineData(ClaudeInstallKind.NotFound, "Not found")]
    public void InstallKindText(ClaudeInstallKind kind, string expected)
    {
        using var rig = new Rig();
        rig.Install.Info = new ClaudeInstallInfo(kind, null, kind == ClaudeInstallKind.NotFound ? null : "target", "claude");
        Assert.Equal(expected, rig.Model.InstallKindText);
    }

    [Fact]
    public void LaunchTargetShowsTheOverrideOrTheDetectedTarget()
    {
        using var rig = new Rig();
        Assert.Equal("Claude_pzs8sxrjxfjjc!Claude", rig.Model.LaunchTargetText);
        Assert.False(rig.Model.CanUseDefaultLaunchTarget);
        rig.Model.ChooseLaunchTarget(@"C:\Tools\claude.exe");
        Assert.Equal(@"C:\Tools\claude.exe", rig.Model.LaunchTargetText);
        Assert.Equal(@"C:\Tools\claude.exe", rig.H.Settings.ClaudeLaunchTarget);
        Assert.True(rig.Model.CanUseDefaultLaunchTarget);
        rig.Model.UseDefaultLaunchTarget();
        Assert.Null(rig.H.Settings.ClaudeLaunchTarget);
        rig.Install.Info = ClaudeInstallInfo.NotFound;
        Assert.Equal("Not found", rig.Model.LaunchTargetText);
    }

    [Fact]
    public void ClaudeConfigPathChooseAndUseDefaultRepointAppState()
    {
        using var rig = new Rig();
        Assert.Equal(rig.H.ClaudeConfigPath, rig.Model.ClaudeConfigPath);
        Assert.False(rig.Model.CanUseDefaultClaudeConfig);
        var other = rig.H.Dir.File(Path.Combine("other", "claude_desktop_config.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        rig.Model.ChooseClaudeConfig(other);
        Assert.Equal(other, rig.Model.ClaudeConfigPath);
        Assert.Equal(other, rig.State.Service.Paths.ClaudeConfigPath);
        Assert.True(rig.Model.CanUseDefaultClaudeConfig);
        rig.Model.UseDefaultClaudeConfig();
        Assert.Equal(rig.H.ClaudeConfigPath, rig.Model.ClaudeConfigPath);
    }

    [Fact]
    public void StringsMatchTheMacApp()
    {
        Assert.Equal("Launch at startup", SettingsModel.LaunchAtStartupTitle);
        Assert.Equal("Confirm before restarting Claude", SettingsModel.ConfirmRestartTitle);
        Assert.Equal("Confirm before quitting", SettingsModel.ConfirmQuitTitle);
        Assert.Equal("Notify about changes made outside Connector Control", SettingsModel.NotifyTitle);
        Assert.Equal("Covers edits to Claude's config and synced connector-list changes, including when a remote change needs a Claude restart.", SettingsModel.NotifyCaption);
        Assert.Equal("Automatically download and install updates", SettingsModel.AutoUpdateTitle);
        Assert.Equal("Check for Updates…", SettingsModel.CheckForUpdatesTitle);
        Assert.Equal("Master List Location", SettingsModel.MasterListHeader);
        Assert.Equal("Choose…", SettingsModel.ChooseTitle);
        Assert.Equal("Both config files are backed up automatically before every change.", SettingsModel.BackupsCaption);
        Assert.Equal("Show in Explorer", SettingsModel.ShowInExplorerTitle);
        Assert.Equal("Restore…", SettingsModel.RestoreTitle);
        Assert.Equal("Claude App", SettingsModel.ClaudeAppHeader);
    }
}

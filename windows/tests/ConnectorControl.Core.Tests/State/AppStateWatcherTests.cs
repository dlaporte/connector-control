using ConnectorControl.Core.Services;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

public class AppStateWatcherTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(1500);
    private static readonly string[] Fixture = ["aws-mcp", "scoutbook", "service-now"];

    [Fact]
    public void ClaudeConfigWatcherRegeneratesAnExternalEdit()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Thread.Sleep(300);
        h.WriteClaudeServers(("scoutbook", state.Store.Mcps["scoutbook"].Config));
        Assert.True(h.Ui.PumpUntil(() => h.Notifier.Sent.Count == 1, Wait));
        Assert.Equal(AppState.ClaudeConfigRegeneratedBody, h.Notifier.Sent[0].Body);
        Assert.Equal(Fixture, AppStateHarness.Keys(h.ClaudeServers().Keys));
        h.Ui.PumpUntil(() => false, Settle);   // the regenerating write echoes through the watcher: it must stay quiet
        Assert.Single(h.Notifier.Sent);
    }

    [Fact]
    public void StoreWatcherAdoptsAnExternalStoreAndAnnouncesTheRestart()
    {
        using var h = new AppStateHarness();
        h.Claude.IsRunning = true;
        h.Claude.LaunchTime = h.Now.AddHours(-1);
        using var state = h.Create();
        Thread.Sleep(300);
        var synced = h.StoreOnDisk();
        synced.Mcps["scoutbook"] = synced.Mcps["scoutbook"] with { Enabled = false };
        MasterStoreIO.Save(synced, h.MasterStorePath);   // another machine's list arrives via sync
        Assert.True(h.Ui.PumpUntil(() => h.Notifier.Sent.Count == 1, Wait));
        Assert.Equal((Notifications.Title, AppState.ConnectorListChangedRestartBody, (string?)Notifications.RestartCategory), h.Notifier.Sent[0]);
        Assert.False(state.Store.Mcps["scoutbook"].Enabled);
        Assert.Equal(["aws-mcp", "service-now"], AppStateHarness.Keys(h.ClaudeServers().Keys));
        Assert.True(state.NeedsClaudeRestart);
    }

    [Fact]
    public void StoreWatcherIgnoresOurOwnWriteEcho()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Thread.Sleep(300);
        state.SetEnabled("aws-mcp", false);
        h.Ui.PumpUntil(() => false, Settle);
        Assert.Empty(h.Notifier.Sent);
        Assert.False(state.Store.Mcps["aws-mcp"].Enabled);
    }

    [Fact]
    public void StoreWatcherIgnoresAnUndecodablePartialWrite()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Thread.Sleep(300);
        File.WriteAllText(h.MasterStorePath, "{\"version\": 2, \"acti");   // a sync tool mid-write
        h.Ui.PumpUntil(() => false, Settle);
        Assert.Equal(Fixture, state.SortedNames);
        Assert.Equal("{\"version\": 2, \"acti", File.ReadAllText(h.MasterStorePath));   // not moved aside: no reload happened
        Assert.Empty(h.Notifier.Sent);
    }

    [Fact]
    public void DeletedStoreFileIsRePersistedFromMemory()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Thread.Sleep(300);
        File.Delete(h.MasterStorePath);
        Assert.True(h.Ui.PumpUntil(() => File.Exists(h.MasterStorePath), Wait));
        Assert.Equal(state.Store, h.StoreOnDisk());
        Assert.Empty(h.Notifier.Sent);
    }

    [Fact]
    public void ReloadArmsOnlyTheWatcherThatCouldNotArmYet()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.True(state.WatchersArmed);
        var later = h.Dir.File(Path.Combine("later", "claude_desktop_config.json"));
        Assert.False(Directory.Exists(Path.GetDirectoryName(later)!));

        // ArmWatchers cannot arm a watcher whose parent directory is missing; the
        // regenerating write inside the same Reload creates it, and the re-arm at the
        // end of Reload catches up (spec §6.3).
        state.RepointClaudeConfig(later);
        Assert.True(File.Exists(later));
        Assert.True(state.WatchersArmed);

        Thread.Sleep(300);
        ClaudeConfigIO.Write(new Dictionary<string, JsonValue> { ["only"] = AppStateHarness.Remote("https://only.example/mcp") }, later);
        Assert.True(h.Ui.PumpUntil(() => h.Notifier.Sent.Count == 1, Wait));   // the new location really is watched
        Assert.Equal(AppState.ClaudeConfigRegeneratedBody, h.Notifier.Sent[0].Body);
    }

    [Fact]
    public void RepointStoreSeedsAnEmptyLocationAndReArmsTheWatcher()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var synced = h.Dir.File("synced");
        state.RepointStore(synced);
        Assert.Equal(synced, h.Settings.MasterStoreDir);
        Assert.Equal(synced, state.Service.Paths.StoreDir);
        Assert.Equal(h.BackupsDir, state.Service.Paths.BackupsDir);   // backups never follow the store
        Assert.Equal(state.Store, MasterStoreIO.Read(Path.Combine(synced, "mcps.json")));
        Assert.True(File.Exists(h.MasterStorePath));   // the previous file is never deleted
        Assert.Empty(h.Notifier.Sent);

        Thread.Sleep(300);
        var edited = state.Store.Clone();
        edited.Mcps["aws-mcp"] = edited.Mcps["aws-mcp"] with { Enabled = false };
        MasterStoreIO.Save(edited, Path.Combine(synced, "mcps.json"));
        Assert.True(h.Ui.PumpUntil(() => !state.Store.Mcps["aws-mcp"].Enabled, Wait));   // the new location is watched
    }

    [Fact]
    public void RepointStoreAdoptsAnExistingStoreQuietly()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var synced = h.Dir.File("synced");
        Directory.CreateDirectory(synced);
        var theirs = state.Store.Clone();
        theirs.Mcps["aws-mcp"] = theirs.Mcps["aws-mcp"] with { Enabled = false };
        theirs.Mcps["synced-only"] = new McpEntry(AppStateHarness.Remote("https://synced.example/mcp"));
        MasterStoreIO.Save(theirs, Path.Combine(synced, "mcps.json"));

        state.RepointStore(synced);
        Assert.Equal(["aws-mcp", "scoutbook", "service-now", "synced-only"], state.SortedNames);
        Assert.False(state.Store.Mcps["aws-mcp"].Enabled);
        Assert.Equal(["scoutbook", "service-now", "synced-only"], AppStateHarness.Keys(h.ClaudeServers().Keys));   // regenerated
        Assert.Empty(h.Notifier.Sent);   // quiet adoption: the user is watching
    }

    [Fact]
    public void RepointStoreBackToTheDefault()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        state.RepointStore(h.Dir.File("synced"));
        state.RepointStore(null);
        Assert.Null(h.Settings.MasterStoreDir);
        Assert.Equal(h.StoreDir, state.Service.Paths.StoreDir);
        Assert.Equal(Fixture, state.SortedNames);
    }

    [Fact]
    public void RefreshServiceSettingsAppliesTheKeepCountWithoutReloading()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        h.Settings.BackupKeepCount = 7;
        state.RefreshServiceSettings();
        Assert.Equal(7, state.Service.Backups.KeepCount);
        Assert.Null(h.Settings.LastApplyDate);
        Assert.Equal(Fixture, state.SortedNames);
    }

    [Fact]
    public void RepointClaudeConfigImportsTheNewFileFreshlyAndQuietly()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var other = h.Dir.File(Path.Combine("other", "claude_desktop_config.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        ClaudeConfigIO.Write(new Dictionary<string, JsonValue> { ["other-only"] = AppStateHarness.Remote("https://other.example/mcp") }, other);

        state.RepointClaudeConfig(other);
        Assert.Equal(other, h.Settings.ClaudeConfigPath);
        Assert.Equal(other, state.Service.Paths.ClaudeConfigPath);
        Assert.Equal(["aws-mcp", "other-only", "scoutbook", "service-now"], state.SortedNames);
        Assert.Equal(["aws-mcp", "other-only", "scoutbook", "service-now"], AppStateHarness.Keys(ClaudeConfigIO.ReadMcpServers(other).Keys));
        Assert.Equal(Fixture, AppStateHarness.Keys(h.ClaudeServers().Keys));   // the old file is left alone
        Assert.Empty(h.Notifier.Sent);

        state.RepointClaudeConfig(null);
        Assert.Null(h.Settings.ClaudeConfigPath);
        Assert.Equal(h.ClaudeConfigPath, state.Service.Paths.ClaudeConfigPath);
    }

    [Fact]
    public void RestoreClaudeConfigIsQuietAndSyncsTheBaseline()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        state.SetEnabled("aws-mcp", false);
        h.Notifier.Sent.Clear();
        h.Now = h.Now.AddMinutes(5);
        var backup = state.Service.Backups.Backups("claude_desktop_config")[0];   // the three-server file from before the toggle

        state.RestoreClaudeConfig(backup);
        Assert.Equal(Fixture, AppStateHarness.Keys(h.ClaudeServers().Keys));
        Assert.True(state.Store.Mcps["aws-mcp"].Enabled);   // adopted into the store
        Assert.Equal(Fixture, AppStateHarness.Keys(state.AppliedServers.Keys));
        Assert.Equal(h.Now, h.Settings.LastApplyDate);
        Assert.False(state.IsDirty);
        Assert.Empty(h.Notifier.Sent);
    }

    [Fact]
    public void RestoreFailurePropagatesWithoutTouchingTheFile()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var bad = h.Dir.File("bad.json");
        File.WriteAllText(bad, "{not json");
        var before = File.ReadAllBytes(h.ClaudeConfigPath);
        var ex = Assert.Throws<ClaudeConfigException>(() => state.RestoreClaudeConfig(bad));
        Assert.Equal("backup bad.json is not a valid config file", ex.Detail);
        Assert.Equal(before, File.ReadAllBytes(h.ClaudeConfigPath));
    }

    [Fact]
    public void DisposeStopsTheWatchers()
    {
        using var h = new AppStateHarness();
        var state = h.Create();
        Thread.Sleep(300);
        state.Dispose();
        h.WriteClaudeServers(("scoutbook", state.Store.Mcps["scoutbook"].Config));
        h.Ui.PumpUntil(() => false, Settle);
        Assert.Empty(h.Notifier.Sent);
        Assert.Equal(["scoutbook"], AppStateHarness.Keys(h.ClaudeServers().Keys));   // nobody regenerated it
    }
}

using ConnectorControl.Core.Services;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

public class AppStateTests
{
    private static readonly string[] Fixture = ["aws-mcp", "scoutbook", "service-now"];

    [Fact]
    public void FirstLoadImportsClaudeServersEnabled()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Equal(Fixture, state.SortedNames);
        Assert.All(state.Store.Mcps.Values, e => Assert.True(e.Enabled));
        Assert.Equal(Fixture, AppStateHarness.Keys(state.AppliedServers.Keys));
        Assert.Empty(h.Notifier.Sent);
        Assert.Null(state.LastError);
        Assert.False(state.IsDirty);
        Assert.False(state.NeedsClaudeRestart);
        Assert.False(state.ApplyRetryNeeded);
        Assert.Equal("3 of 3 enabled", state.HeaderSubtitle);
        Assert.Equal(["Default"], state.ProfileNames);
        Assert.Equal("Default", state.ActiveProfile);
        Assert.True(File.Exists(h.MasterStorePath));
        Assert.True(h.Settings.AclSweepDone);
    }

    [Fact]
    public void HeaderSubtitleForAnEmptyStore()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        Assert.Equal("No connectors configured", state.HeaderSubtitle);
        Assert.Empty(state.SortedNames);
    }

    [Fact]
    public void SetEnabledPersistsAndAppliesImmediately()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        state.SetEnabled("aws-mcp", false);
        Assert.Equal(["scoutbook", "service-now"], AppStateHarness.Keys(h.ClaudeServers().Keys));
        Assert.False(h.StoreOnDisk().Mcps["aws-mcp"].Enabled);
        Assert.Equal(h.Now, h.Settings.LastApplyDate);
        Assert.Equal("2 of 3 enabled", state.HeaderSubtitle);
        Assert.False(state.IsDirty);
        Assert.Single(state.Service.Backups.Backups("claude_desktop_config"));
        Assert.True(File.Exists(Path.Combine(h.BackupsDir, "claude_desktop_config.original.json")));
        Assert.Empty(h.Notifier.Sent);
    }

    [Fact]
    public void RestartRequiredFollowsClaudeLaunchTime()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        h.Claude.IsRunning = true;
        h.Claude.LaunchTime = h.Now.AddHours(-1);
        state.SetEnabled("aws-mcp", false);
        Assert.True(state.NeedsClaudeRestart);

        h.Claude.LaunchTime = h.Now.AddMinutes(1);   // Claude relaunched after our write
        state.RefreshRestartState();
        Assert.False(state.NeedsClaudeRestart);

        h.Claude.LaunchTime = h.Now.AddHours(-1);
        h.Claude.IsRunning = false;                  // not running ⇒ never "required"
        state.RefreshRestartState();
        Assert.False(state.NeedsClaudeRestart);
    }

    [Fact]
    public void RestartRequiredAtLaunchFromAPersistedApplyDate()
    {
        using var h = new AppStateHarness();
        h.Settings.LastApplyDate = h.Now;
        h.Claude.IsRunning = true;
        h.Claude.LaunchTime = h.Now.AddHours(-1);
        using var state = h.Create();
        Assert.True(state.NeedsClaudeRestart);
    }

    [Fact]
    public void UpsertValidatesNames()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var entry = new McpEntry(AppStateHarness.Remote("https://new.example/mcp"));
        Assert.Equal("Name must not be empty.", state.Upsert("", entry, null));
        Assert.Equal("Name must not be empty.", state.Upsert(" \t ", entry, null));
        Assert.Equal("A connector named “scoutbook” already exists.", state.Upsert("scoutbook", entry, null));
        Assert.Null(state.Upsert("scoutbook", entry, "scoutbook"));   // saving under its own name replaces
        Assert.Equal(entry.Config, state.Store.Mcps["scoutbook"].Config);
        Assert.Null(state.Upsert(" new ", entry, null));              // spaces trimmed
        Assert.True(state.Store.Mcps.ContainsKey("new"));
    }

    [Fact]
    public void UpsertPersistsButOnlyInteractiveApplyWritesClaudesConfig()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.Upsert("new", new McpEntry(AppStateHarness.Remote("https://new.example/mcp")), null));
        Assert.True(h.StoreOnDisk().Mcps.ContainsKey("new"));
        Assert.False(h.ClaudeServers().ContainsKey("new"));
        Assert.True(state.IsDirty);
        state.ApplyInteractively();
        Assert.True(h.ClaudeServers().ContainsKey("new"));
        Assert.False(state.IsDirty);
    }

    [Fact]
    public void UpsertRenameRemovesTheOldKey()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var entry = state.Store.Mcps["scoutbook"];
        Assert.Null(state.Upsert("scoutbook2", entry, "scoutbook"));
        Assert.False(state.Store.Mcps.ContainsKey("scoutbook"));
        Assert.True(state.Store.Mcps.ContainsKey("scoutbook2"));
        Assert.False(h.StoreOnDisk().Mcps.ContainsKey("scoutbook"));
    }

    [Fact]
    public void RemovePersistsButDoesNotApply()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        state.Remove("aws-mcp");
        Assert.False(h.StoreOnDisk().Mcps.ContainsKey("aws-mcp"));
        Assert.True(h.ClaudeServers().ContainsKey("aws-mcp"));
        Assert.True(state.IsDirty);
        state.ApplyInteractively();
        Assert.False(h.ClaudeServers().ContainsKey("aws-mcp"));
    }

    [Fact]
    public void ApplyInteractivelyIsANoOpWhenCleanButApplyAlwaysWrites()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        state.ApplyInteractively();
        Assert.Null(h.Settings.LastApplyDate);
        state.Apply();
        Assert.Equal(h.Now, h.Settings.LastApplyDate);
    }

    [Fact]
    public void PendingRemovalIsRegeneratedQuietlyOnReload()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        state.Remove("aws-mcp");
        state.Reload();
        Assert.False(h.ClaudeServers().ContainsKey("aws-mcp"));   // regenerated from the store
        Assert.False(state.Store.Mcps.ContainsKey("aws-mcp"));    // not resurrected: it matched the baseline
        Assert.Empty(h.Notifier.Sent);                             // our own change is not "external"
        Assert.False(state.IsDirty);
    }

    [Fact]
    public void ExternalEditOfClaudesConfigIsRegeneratedAndAnnounced()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var original = state.Store.Mcps["scoutbook"].Config;
        h.WriteClaudeServers(
            ("scoutbook", AppStateHarness.Remote("https://changed.example/mcp")),
            ("service-now", state.Store.Mcps["service-now"].Config),
            ("newcomer", AppStateHarness.Remote("https://newcomer.example/mcp")));
        state.Reload();
        Assert.Equal(["aws-mcp", "newcomer", "scoutbook", "service-now"], state.SortedNames);
        Assert.True(state.Store.Mcps["newcomer"].Enabled);
        Assert.Equal(original, state.Store.Mcps["scoutbook"].Config);                    // known entries are never modified by the file
        Assert.Equal(original, h.ClaudeServers()["scoutbook"]);                          // and the file is regenerated from the store
        Assert.True(h.ClaudeServers().ContainsKey("aws-mcp"));
        Assert.Single(h.Notifier.Sent);
        Assert.Equal((Notifications.Title, AppState.ClaudeConfigRegeneratedBody, (string?)null), h.Notifier.Sent[0]);
        Assert.Equal(h.Now, h.Settings.LastApplyDate);
        Assert.False(state.IsDirty);
    }

    [Fact]
    public void ExternalRemovalIsRegeneratedAndAnnounced()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        h.WriteClaudeServers(("scoutbook", state.Store.Mcps["scoutbook"].Config));
        state.Reload();
        Assert.Equal(Fixture, AppStateHarness.Keys(h.ClaudeServers().Keys));
        Assert.Equal([AppState.ClaudeConfigRegeneratedBody], h.Notifier.Sent.Select(s => s.Body).ToArray());
    }

    [Fact]
    public void ExternalEditThatMatchesTheStoreOnlyAnnouncesTheChange()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        state.Remove("aws-mcp");   // pending removal: store and file now differ
        h.WriteClaudeServers(      // someone writes exactly what the store would render
            ("scoutbook", state.Store.Mcps["scoutbook"].Config),
            ("service-now", state.Store.Mcps["service-now"].Config));
        state.Reload();
        Assert.Equal([AppState.ClaudeConfigChangedBody], h.Notifier.Sent.Select(s => s.Body).ToArray());
        Assert.Null(h.Settings.LastApplyDate);   // nothing to regenerate
        Assert.False(state.IsDirty);
    }

    [Fact]
    public void StoreEditedOutsideWithoutRegenerationIsAnnounced()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        state.SetEnabled("aws-mcp", false);
        h.Notifier.Sent.Clear();
        var edited = h.StoreOnDisk();
        edited.Mcps["aws-mcp"] = edited.Mcps["aws-mcp"] with { Config = AppStateHarness.Remote("https://synced.example/mcp") };
        MasterStoreIO.Save(edited, h.MasterStorePath);   // a sync tool wrote the store; the disabled entry needs no regeneration
        state.Reload();
        Assert.Equal([AppState.StoreChangedBody], h.Notifier.Sent.Select(s => s.Body).ToArray());
        Assert.Equal(AppStateHarness.Remote("https://synced.example/mcp"), state.Store.Mcps["aws-mcp"].Config);
    }

    [Fact]
    public void MalformedClaudeConfigReportsTheNoteAndBlocksApply()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        File.WriteAllText(h.ClaudeConfigPath, "{oops");
        state.Reload();
        Assert.Equal("Claude's config file is not valid JSON. Your MCP list is safe; use Backups ▸ Restore… to repair the file.", state.LastError);
        Assert.Equal(Fixture, state.SortedNames);
        Assert.Empty(h.Notifier.Sent);
        Assert.False(state.ApplyRetryNeeded);

        state.SetEnabled("aws-mcp", false);
        Assert.True(state.ApplyRetryNeeded);
        Assert.StartsWith("Claude's config file is not valid JSON (", state.LastError, StringComparison.Ordinal);
        Assert.EndsWith("). Nothing was written. Use Backups ▸ Restore… to recover it.", state.LastError, StringComparison.Ordinal);
        Assert.False(h.StoreOnDisk().Mcps["aws-mcp"].Enabled);   // the store change persisted even though the apply failed
        Assert.Equal("{oops", File.ReadAllText(h.ClaudeConfigPath));

        File.WriteAllText(h.ClaudeConfigPath, Fixtures.RealisticClaudeConfig);   // the user repaired the file
        state.Reload();
        Assert.False(state.ApplyRetryNeeded);
        Assert.Null(state.LastError);
        Assert.Equal(["scoutbook", "service-now"], AppStateHarness.Keys(h.ClaudeServers().Keys));
        Assert.Empty(h.Notifier.Sent);   // a retry that succeeds is the user's own change, not an external one
    }

    [Fact]
    public void RegenerationFailureIsAnnouncedOnceOnTheTransitionOnly()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        h.WriteClaudeServers(("scoutbook", state.Store.Mcps["scoutbook"].Config));   // external edit needing regeneration
        using (var block = new WriteBlock(h.ClaudeConfigPath))
        {
            // Asserted, never branched on: if neither mechanism binds the rest of this
            // test proves nothing, and CI fails skips, so it has to fail here instead.
            Assert.True(block.IsEffective);
            state.Reload();
            Assert.True(state.ApplyRetryNeeded);
            Assert.Equal([AppState.RegenerationFailedBody], h.Notifier.Sent.Select(s => s.Body).ToArray());
            state.Reload();   // every flyout open retries; the failure must not be re-announced
            Assert.True(state.ApplyRetryNeeded);
            Assert.Single(h.Notifier.Sent);
        }
        state.Reload();
        Assert.False(state.ApplyRetryNeeded);
        Assert.Equal(Fixture, AppStateHarness.Keys(h.ClaudeServers().Keys));
        Assert.Single(h.Notifier.Sent);   // the eventual success is quiet
    }

    [Fact]
    public void CorruptStoreIsRebuiltWithANote()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        File.WriteAllText(h.MasterStorePath, "garbage");
        state.Reload();
        Assert.StartsWith("The MCP list file was unreadable; it was preserved as mcps.corrupt.", state.LastError, StringComparison.Ordinal);
        Assert.Equal(Fixture, state.SortedNames);
        Assert.Empty(h.Notifier.Sent);
    }

    [Fact]
    public void ReloadOverwritesLastError()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        state.LastError = "stale";
        state.Reload();
        Assert.Null(state.LastError);
    }

    [Fact]
    public void FriendlyMapsMalformedConfigAndPassesOtherMessagesThrough()
    {
        Assert.Equal(
            "Claude's config file is not valid JSON (top level is not a JSON object). Nothing was written. Use Backups ▸ Restore… to recover it.",
            AppState.Friendly(new ClaudeConfigException("top level is not a JSON object")));
        Assert.Equal("disk full", AppState.Friendly(new IOException("disk full")));
    }

    [Fact]
    public void MakeServiceHonorsSettingsAndKeepsBackupsMachineLocal()
    {
        using var h = new AppStateHarness();
        h.Settings.MasterStoreDir = h.Dir.File("synced");
        h.Settings.BackupKeepCount = 7;
        var service = AppState.MakeService(h.Settings, h.Context);
        Assert.Equal(h.Dir.File("synced"), service.Paths.StoreDir);
        Assert.Equal(h.BackupsDir, service.Paths.BackupsDir);
        Assert.Equal(h.ClaudeConfigPath, service.Paths.ClaudeConfigPath);
        Assert.Equal(7, service.Backups.KeepCount);
    }

    [Fact]
    public void RefreshToolsProbesOffTheUiThreadAndPublishesThroughTheHost()
    {
        using var h = new AppStateHarness();
        h.Tools.Statuses[Tool.Uvx] = ToolStatus.NotFound;
        using var state = h.Create();
        Assert.Empty(state.ToolStatuses);   // nothing is probed until the editor or Settings asks
        var raised = new List<string?>();
        state.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var task = state.RefreshToolsAsync();
        Assert.True(h.Ui.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5)));
        Assert.Equal(4, state.ToolStatuses.Count);
        Assert.False(state.ToolStatuses[Tool.Uvx].Found);
        Assert.Equal("1.0.0", state.ToolStatuses[Tool.Npx].Version);
        Assert.Contains(nameof(AppState.ToolStatuses), raised);
        Assert.Equal(ToolInfo.All.ToArray(), h.Tools.Probed.ToArray());
        Assert.Equal(1, h.Tools.Batches);
    }

    [Fact]
    public void RefreshToolsDoesNotProbeAToolAlreadyInFlight()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var first = state.RefreshToolsAsync([Tool.Npx]);
        var second = state.RefreshToolsAsync([Tool.Npx, Tool.Node]);   // npx joins the flight already in the air; node starts one
        Assert.True(h.Ui.PumpUntil(() => first.IsCompleted && second.IsCompleted, TimeSpan.FromSeconds(5)));
        Assert.Equal([Tool.Npx, Tool.Node], h.Tools.Probed.Order().ToArray());
        Assert.Equal(2, state.ToolStatuses.Count);
        Assert.True(state.RefreshToolsAsync([]).IsCompleted);   // nothing wanted: completes synchronously
        // Once published, the same tool can be probed again (the editor asks when the command changes).
        var third = state.RefreshToolsAsync([Tool.Npx]);
        Assert.True(h.Ui.PumpUntil(() => third.IsCompleted, TimeSpan.FromSeconds(5)));
        Assert.Equal(3, h.Tools.Probed.Count);
    }
}

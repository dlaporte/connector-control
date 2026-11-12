using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// Restoring one collection from a master-list backup (<c>CollectionRestorePlan</c>, <c>RestoreCollection</c>).
///
/// Mirror: Tests/ConnectorControlStateTests/AppStateCollectionRestoreTests.swift
/// </summary>
public class AppStateCollectionRestoreTests
{
    /// <summary><paramref name="store"/> saved where the backups are, as a master-list backup holds it: its
    /// file and bytes. Named older than any backup a save takes, so the newest in the series is still the
    /// save's own.</summary>
    private static (string Path, byte[] Data) Backup(AppStateHarness h, MasterStore store, string name = "mcps.2026-01-01T00-00-00-000Z.json")
    {
        Directory.CreateDirectory(h.BackupsDir);
        var path = Path.Combine(h.BackupsDir, name);
        MasterStoreIO.Save(store, path);
        return (path, File.ReadAllBytes(path));
    }

    private static JsonValue Config(string command) => JsonValue.Object(("command", JsonValue.String(command)));

    private static IReadOnlyList<string> McpsBackups(AppState state) => state.Service.Backups.Backups("mcps");

    /// <summary>The reported sequence. Personal, with 11 connectors and 2 on, is deleted. A Claude's config restore
    /// of a backup recorded against it, holding the one connector Claude was running, creates Personal again with
    /// that one, active. Restoring Personal from the master-list backup taken before the delete then brings back
    /// all 11 with the same 2 on, and leaves Harvard alone.</summary>
    [Fact]
    public void TheReportedSequenceBringsPersonalBackWhole()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.AddEmptyCollection("Personal"));
        for (var index = 1; index <= 11; index++)
        {
            var name = $"p{index:D2}";
            Assert.Null(state.Upsert(name, new McpEntry(index <= 2, Config(name)), null, "Personal"));
        }
        Assert.Null(state.AddEmptyCollection("Harvard"));
        Assert.Null(state.Upsert("canvas", new McpEntry(Config("canvas")), null, "Harvard"));
        state.SwitchCollection("Harvard");
        var personal = state.Store.Collections["Personal"].Clone();
        var harvard = state.Store.Collections["Harvard"].Clone();
        // What Claude ran from Personal once: one connector, recorded against Personal.
        Directory.CreateDirectory(h.BackupsDir);
        var claudeBackup = Path.Combine(h.BackupsDir, "claude_desktop_config.2026-11-01T00-00-00-000Z.json");
        File.WriteAllText(claudeBackup, """{"mcpServers": {"p01": {"command": "p01"}}}""");
        BackupCollections.Record("Personal", claudeBackup, h.BackupsDir);

        Assert.Null(state.DeleteCollection("Personal"));
        state.RestoreClaudeConfig(claudeBackup);
        Assert.Equal("Personal", state.ActiveCollection);
        Assert.Equal(["p01"], AppStateHarness.Keys(state.Store.Collections["Personal"].Mcps.Keys));

        // Newest first, the first backup holding all 11 is the one the delete took, and pinned.
        var before = McpsBackups(state).First(path => MasterStoreIO.Read(path)?.Collections.GetValueOrDefault("Personal")?.Mcps.Count == 11);
        Assert.Contains(Path.GetFileName(before), BackupPins.Pinned(h.BackupsDir));
        var data = BoundedRead.Data(before, ConfigService.MaxBackupBytes);
        var plan = state.CollectionRestorePlan("Personal", Path.GetFileName(before), data);
        Assert.False(plan.Created);
        Assert.True(plan.Active);
        Assert.Equal(["+ p02 (on)", .. Enumerable.Range(3, 9).Select(i => $"+ p{i:D2} (off)"), "= 1 unchanged"], plan.Lines());

        state.RestoreCollection("Personal", before, data);
        Assert.Equal(personal, state.Store.Collections["Personal"]);
        Assert.Equal(2, state.Store.Collections["Personal"].Mcps.Values.Count(e => e.Enabled));
        Assert.Equal(harvard, state.Store.Collections["Harvard"]);
        Assert.Equal("Personal", state.ActiveCollection);
        Assert.Equal(["p01", "p02"], AppStateHarness.Keys(h.ClaudeServers().Keys));
    }

    /// <summary>Exactly as it was: a connector added since goes, a changed config and a changed switch go back,
    /// and one deleted since comes back. The lines run by name, whatever each one does.</summary>
    [Fact]
    public void ACollectionGoesBackExactlyAsItWas()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var before = state.Store.Collections["Default"].Clone();
        var taken = Backup(h, state.Store);
        Assert.Null(state.Upsert("added-since", new McpEntry(Config("a")), null));
        Assert.Null(state.Upsert("aws-mcp", new McpEntry(Config("changed")), "aws-mcp"));
        state.SetEnabled("scoutbook", false);
        state.Delete(["service-now"]);

        var plan = state.CollectionRestorePlan("Default", Path.GetFileName(taken.Path), taken.Data);
        Assert.Equal("Default", plan.Collection);
        Assert.False(plan.Created);
        Assert.True(plan.Active);
        Assert.Equal(["− added-since", "~ aws-mcp (on)", "~ scoutbook (on)", "+ service-now (on)"], plan.Lines());
        Assert.Equal("− <added-since>", plan.Lines(name => $"<{name}>")[0]);   // the caller names each connector

        state.RestoreCollection("Default", taken.Path, taken.Data);
        Assert.Equal(before, state.Store.Collections["Default"]);
        Assert.Equal(before, h.StoreOnDisk().Collections["Default"]);
        Assert.Equal("Default", state.ActiveCollection);
    }

    /// <summary>Each refusal, in the order they are checked; none of them writes anything.</summary>
    [Fact]
    public void EachRefusalInOrder()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        h.Subscribe(state, CollectionDocumentSamples.DataTeam);
        var taken = Backup(h, state.Store);
        var id = Path.GetFileName(taken.Path);
        var storeBefore = state.Store.Clone();
        var backupsBefore = McpsBackups(state);
        // The plan's refusal, which the restore itself gives too when it is handed the same bytes.
        CollectionRestoreException Refusal(string collection, byte[] data)
        {
            var planned = Assert.ThrowsAny<CollectionRestoreException>(() => state.CollectionRestorePlan(collection, id, data));
            var restored = Assert.ThrowsAny<CollectionRestoreException>(() => state.RestoreCollection(collection, taken.Path, data));
            Assert.Equal(planned.GetType(), restored.GetType());
            Assert.Equal(planned.Message, restored.Message);
            return planned;
        }

        // Bytes that could not be read.
        var unread = Assert.IsType<CollectionBackupUnreadableException>(
            Assert.ThrowsAny<CollectionRestoreException>(() => state.CollectionRestorePlan("Data team", id, null)));
        Assert.Equal(id, unread.Id);
        // Unreadable comes first.
        var unreadable = Assert.IsType<CollectionBackupUnreadableException>(Refusal("Data team", "{not json"u8.ToArray()));
        Assert.Equal(AppState.CollectionBackupUnreadableError(id), unreadable.Message);
        var notInBackup = Assert.IsType<CollectionNotInBackupException>(Refusal("Nope", taken.Data));
        Assert.Equal((id, "Nope"), (notInBackup.Id, notInBackup.Collection));
        Assert.Equal(AppState.CollectionNotInBackupError(id, "Nope"), notInBackup.Message);
        // Subscribed before unchanged.
        var subscribed = Assert.IsType<RestoreCollectionSubscribedException>(Refusal("Data team", taken.Data));
        Assert.Equal("Data team", subscribed.Collection);
        Assert.Equal(AppState.RestoreCollectionSubscribedError("Data team"), subscribed.Message);
        var unchanged = Assert.IsType<RestoreCollectionUnchangedException>(Refusal("Default", taken.Data));
        Assert.Equal("Default", unchanged.Collection);
        Assert.Equal(AppState.RestoreCollectionUnchangedError("Default"), unchanged.Message);

        Assert.Equal(storeBefore, state.Store);
        Assert.Equal(backupsBefore, McpsBackups(state));   // nothing was saved
    }

    /// <summary>A backup whose bytes could not be read is refused as unreadable, in the plan.</summary>
    [Fact]
    public void ABackupWhoseBytesCouldNotBeReadIsRefusedAsUnreadable()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var missing = Path.Combine(h.BackupsDir, "mcps.2026-01-02T00-00-00-000Z.json");
        var refused = Assert.Throws<CollectionBackupUnreadableException>(
            () => state.CollectionRestorePlan("Default", Path.GetFileName(missing), null));
        Assert.Equal(Path.GetFileName(missing), refused.Id);
    }

    /// <summary>A collection that was subscribed when the backup was taken and is gone now comes back local: the
    /// backup holds its connectors, never its subscription.</summary>
    [Fact]
    public void AGoneCollectionThatWasSubscribedComesBackLocal()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        h.Subscribe(state, CollectionDocumentSamples.DataTeam);
        var authored = state.Store.Collections["Data team"].Clone();
        var taken = Backup(h, state.Store);
        Assert.Null(state.DeleteCollection("Data team"));

        var plan = state.CollectionRestorePlan("Data team", Path.GetFileName(taken.Path), taken.Data);
        Assert.True(plan.Created);
        Assert.False(plan.Active);
        state.RestoreCollection("Data team", taken.Path, taken.Data);
        Assert.Equal(authored, state.Store.Collections["Data team"]);
        Assert.Equal(CollectionKind.Local, state.KindOf("Data team"));
        Assert.False(state.CollectionsFile.Collections.ContainsKey("Data team"));   // no sidecar entry
        Assert.Equal("Default", state.ActiveCollection);
    }

    /// <summary>A collection the store holds under another spelling of its name (é composed, the backup's
    /// decomposed) is restored into the spelling held, never beside it.</summary>
    [Fact]
    public void AnotherSpellingOfTheNameRestoresIntoTheOneHeld()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        const string composed = "Café", decomposed = "Café";
        Assert.Null(state.AddEmptyCollection(composed));
        Assert.Null(state.Upsert("now", new McpEntry(Config("n")), null, composed));
        var then = new Collection([new KeyValuePair<string, McpEntry>("then", new McpEntry(Config("t")))]);
        var other = state.Store.Clone();
        other.Collections.Remove(composed);
        other.Collections[decomposed] = then;
        var taken = Backup(h, other);

        var plan = state.CollectionRestorePlan(decomposed, Path.GetFileName(taken.Path), taken.Data);
        Assert.Equal(composed, plan.Collection);
        Assert.False(plan.Created);
        state.RestoreCollection(decomposed, taken.Path, taken.Data);
        Assert.Equal(composed, Assert.Single(state.Store.Collections.Keys, k => k.StartsWith("Caf", StringComparison.Ordinal)));
        Assert.Equal(then, state.Store.Collections[composed]);
    }

    /// <summary>Restoring the active collection updates Claude's config; restoring another touches nothing Claude runs.</summary>
    [Fact]
    public void OnlyTheActiveCollectionsRestoreReachesClaude()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.AddEmptyCollection("Work"));
        Assert.Null(state.Upsert("w1", new McpEntry(Config("w1")), null, "Work"));
        Assert.Null(state.Upsert("w2", new McpEntry(Config("w2")), null, "Work"));
        var taken = Backup(h, state.Store);
        state.SetEnabled("w1", false, "Work");
        state.SetEnabled("aws-mcp", false);
        Assert.False(h.ClaudeServers().ContainsKey("aws-mcp"));
        var claudeBefore = File.ReadAllBytes(h.ClaudeConfigPath);
        var appliedBefore = h.Settings.LastApplyDate;

        var work = state.CollectionRestorePlan("Work", Path.GetFileName(taken.Path), taken.Data);
        Assert.False(work.Active);
        Assert.Equal(["~ w1 (on)", "= 1 unchanged"], work.Lines());
        state.RestoreCollection("Work", taken.Path, taken.Data);
        Assert.True(state.Store.Collections["Work"].Mcps["w1"].Enabled);
        Assert.Equal(claudeBefore, File.ReadAllBytes(h.ClaudeConfigPath));   // Claude does not run Work
        Assert.Equal(appliedBefore, h.Settings.LastApplyDate);

        h.Now = h.Now.AddSeconds(60);
        Assert.True(state.CollectionRestorePlan("Default", Path.GetFileName(taken.Path), taken.Data).Active);
        state.RestoreCollection("Default", taken.Path, taken.Data);
        Assert.True(h.ClaudeServers().ContainsKey("aws-mcp"));   // Claude runs Default
        Assert.Equal(h.Now, h.Settings.LastApplyDate);
        Assert.Equal("Default", state.ActiveCollection);
    }

    /// <summary>The restore keeps the backup it takes for 30 days, so it can be undone the same way.</summary>
    [Fact]
    public void TheRestoresOwnBackupIsPinned()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var taken = Backup(h, state.Store);
        state.SetEnabled("aws-mcp", false);
        Assert.Empty(BackupPins.Pinned(h.BackupsDir));

        state.RestoreCollection("Default", taken.Path, taken.Data);
        var newest = McpsBackups(state)[0];
        Assert.NotEqual(taken.Path, newest);
        Assert.Equal([Path.GetFileName(newest)], BackupPins.Pinned(h.BackupsDir));
        Assert.False(MasterStoreIO.Read(newest)!.Collections["Default"].Mcps["aws-mcp"].Enabled);   // the pinned copy holds what the restore replaced
    }

    /// <summary>A connector the restore removes from a published collection loses its marks, as a delete takes them.</summary>
    [Fact]
    public void AConnectorTheRestoreRemovesLosesItsPublishIntent()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var taken = Backup(h, state.Store);
        const string path = "/Users/d/ledger/dist/index.js";
        Assert.Null(state.Upsert("ledger", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String("node")), ("args", JsonValue.Array([JsonValue.String(path)])))), null));
        var intent = new PublishIntent(
            [],
            [new("ledger", new Dictionary<JsonPointer, PublishIntent.PathMark>
            {
                [new JsonPointer(["args", "0"])] = new("server_path", "your ledger clone", path),
            })],
            []);
        var document = h.Publish(state, "Default", intent);
        Assert.True(state.CollectionsFile.Collections["Default"].Publish!.Intent.PathMarks.ContainsKey("ledger"));

        state.RestoreCollection("Default", taken.Path, taken.Data);
        Assert.False(state.Store.Collections["Default"].Mcps.ContainsKey("ledger"));
        Assert.False(state.CollectionsFile.Collections["Default"].Publish!.Intent.PathMarks.ContainsKey("ledger"));
        Assert.Null(state.PublishError);
        Assert.False(CollectionDocument.Decode(File.ReadAllBytes(document)).Connectors.ContainsKey("ledger"));
    }

    /// <summary>A published collection keeps the marks of every connector the restore leaves in place: only one it
    /// removes loses its own, and the document still carries the placeholder, never the path.</summary>
    [Fact]
    public void APublishedCollectionKeepsTheMarksOfWhatStays()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        const string path = "/Users/d/ledger/dist/index.js";
        Assert.Null(state.Upsert("ledger", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String("node")), ("args", JsonValue.Array([JsonValue.String(path)])))), null));
        var taken = Backup(h, state.Store);
        Assert.Null(state.Upsert("added-since", new McpEntry(Config("a")), null));
        var mark = new PublishIntent.PathMark("server_path", "your ledger clone", path);
        var pointer = new JsonPointer(["args", "0"]);
        var document = h.Publish(state, "Default", new PublishIntent(
            [],
            [new("ledger", new Dictionary<JsonPointer, PublishIntent.PathMark> { [pointer] = mark })],
            []));

        state.RestoreCollection("Default", taken.Path, taken.Data);
        Assert.False(state.Store.Collections["Default"].Mcps.ContainsKey("added-since"));
        var kept = Assert.Single(state.CollectionsFile.Collections["Default"].Publish!.Intent.PathMarks["ledger"]);
        Assert.Equal((pointer, mark), (kept.Key, kept.Value));
        Assert.Null(state.PublishError);
        var published = CollectionDocument.Decode(File.ReadAllBytes(document));
        Assert.False(published.Connectors.ContainsKey("added-since"));
        Assert.Equal(["server_path"], published.Connectors["ledger"].Needs.Keys.Order(StringComparer.Ordinal));
        Assert.False(JsonText.FileContains(document, path));
    }

    /// <summary>A Claude's config backup recorded under another spelling of a collection's name (decomposed, the
    /// store's composed) goes back into the collection held, which becomes active: it is not gone, so nothing is
    /// created and the confirmation says nothing of it.</summary>
    [Fact]
    public void AClaudeConfigBackupRecordedUnderAnotherSpellingGoesIntoTheOneHeld()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        const string composed = "Caf\u00E9", decomposed = "Cafe\u0301";
        Assert.Null(state.AddEmptyCollection(composed));
        Directory.CreateDirectory(h.BackupsDir);
        var claudeBackup = Path.Combine(h.BackupsDir, "claude_desktop_config.2026-11-01T00-00-00-000Z.json");
        File.WriteAllText(claudeBackup, """{"mcpServers": {"c1": {"command": "c1"}}}""");
        BackupCollections.Record(decomposed, claudeBackup, h.BackupsDir);

        var model = new RestoreModel(state, h.Dialogs);
        model.Load();
        model.Selection = claudeBackup;
        Assert.True(model.Restore());
        Assert.Null(Assert.Single(h.Dialogs.Confirms).Informative);
        Assert.Equal(composed, state.ActiveCollection);
        Assert.Equal(composed, Assert.Single(state.Store.Collections.Keys, k => k.StartsWith("Caf", StringComparison.Ordinal)));
        Assert.True(state.Store.Collections[composed].Mcps.ContainsKey("c1"));
    }

    /// <summary>For a collection this machine publishes, a connector that comes back has no review: the restore
    /// lands in the store, and nothing reaches the folder until the Publish sheet reviews it.</summary>
    [Fact]
    public void AConnectorThatComesBackToAPublishedCollectionIsHeldForReview()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.Upsert("extra", new McpEntry(Config("x")), null));
        var taken = Backup(h, state.Store);
        var document = h.Publish(state, "Default");
        state.Delete(["extra"]);
        Assert.Null(state.PublishError);
        var published = File.ReadAllBytes(document);

        state.RestoreCollection("Default", taken.Path, taken.Data);
        Assert.True(state.Store.Collections["Default"].Mcps.ContainsKey("extra"));
        Assert.Equal(published, File.ReadAllBytes(document));   // nothing reached the folder
        Assert.Equal("Default", state.PublishError?.Collection);
        Assert.Equal(AppState.UnreviewedConnectorError("extra"), state.PublishError?.Message);
        Assert.Equal(PublishErrorKind.BlockedForReview, state.PublishError?.Kind);
    }
}

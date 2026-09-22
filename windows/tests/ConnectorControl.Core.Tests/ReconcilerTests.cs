namespace ConnectorControl.Core.Tests;

public class ReconcilerTests
{
    private static readonly JsonValue ConfigA = JsonValue.Object(("command", JsonValue.String("a")));
    private static readonly JsonValue ConfigB = JsonValue.Object(("command", JsonValue.String("b")));

    private static MasterStore Store(params (string Name, McpEntry Entry)[] mcps) =>
        new(mcps.Select(m => new KeyValuePair<string, McpEntry>(m.Name, m.Entry)));

    private static Dictionary<string, JsonValue> Servers(params (string Name, JsonValue Config)[] servers) =>
        servers.ToDictionary(s => s.Name, s => s.Config, StringComparer.Ordinal);

    // ingestion — the only file→store flow

    [Fact]
    public void UnknownServerIsImportedEnabled()
    {
        var outcome = Reconciler.Reconcile(MasterStore.Empty(), Servers(("new", ConfigA)));
        Assert.Equal(new McpEntry(true, ConfigA, EditView.Form), outcome.Store.Mcps["new"]);
        Assert.True(outcome.StoreChanged);
    }

    [Fact]
    public void PendingRemovalNotResurrected()
    {
        var outcome = Reconciler.Reconcile(MasterStore.Empty(), Servers(("gone", ConfigA)), Servers(("gone", ConfigA)));
        Assert.False(outcome.Store.Mcps.ContainsKey("gone"));
        Assert.False(outcome.StoreChanged);
    }

    [Fact]
    public void ExternallyAddedServerImportsMidSession()
    {
        var outcome = Reconciler.Reconcile(MasterStore.Empty(), Servers(("new", ConfigA)), Servers());
        Assert.Equal(new McpEntry(true, ConfigA, EditView.Form), outcome.Store.Mcps["new"]);
        Assert.True(outcome.StoreChanged);
    }

    // store is the source of truth — the file never edits known entries

    [Fact]
    public void ExternalEditDoesNotChangeStore()
    {
        var outcome = Reconciler.Reconcile(Store(("s", new McpEntry(true, ConfigA))), Servers(("s", ConfigB)));
        Assert.Equal(ConfigA, outcome.Store.Mcps["s"].Config);
        Assert.False(outcome.StoreChanged);
    }

    [Fact]
    public void ExternalEditWithChangedBaselineDoesNotChangeStore()
    {
        var outcome = Reconciler.Reconcile(Store(("s", new McpEntry(true, ConfigA))), Servers(("s", ConfigB)), Servers(("s", ConfigA)));
        Assert.Equal(ConfigA, outcome.Store.Mcps["s"].Config);
        Assert.False(outcome.StoreChanged);
    }

    [Fact]
    public void PendingEditSurvivesReloadWhenFileUnchanged()
    {
        var outcome = Reconciler.Reconcile(Store(("s", new McpEntry(true, ConfigB))), Servers(("s", ConfigA)), Servers(("s", ConfigA)));
        Assert.Equal(ConfigB, outcome.Store.Mcps["s"].Config);
        Assert.False(outcome.StoreChanged);
    }

    [Fact]
    public void DisabledEntryStaysDisabledWhenPresentInFile()
    {
        var outcome = Reconciler.Reconcile(Store(("s", new McpEntry(false, ConfigA))), Servers(("s", ConfigA)), Servers());
        Assert.False(outcome.Store.Mcps["s"].Enabled);
        Assert.False(outcome.StoreChanged);
    }

    [Fact]
    public void DisabledEntryStaysDisabledWhenExternallyModified()
    {
        var outcome = Reconciler.Reconcile(Store(("s", new McpEntry(false, ConfigA))), Servers(("s", ConfigB)), Servers(("s", ConfigA)));
        Assert.False(outcome.Store.Mcps["s"].Enabled);
        Assert.Equal(ConfigA, outcome.Store.Mcps["s"].Config);
        Assert.False(outcome.StoreChanged);
    }

    [Fact]
    public void PendingDisableSurvivesReloadWhenFileUnchanged()
    {
        var outcome = Reconciler.Reconcile(Store(("s", new McpEntry(false, ConfigA))), Servers(("s", ConfigA)), Servers(("s", ConfigA)));
        Assert.False(outcome.Store.Mcps["s"].Enabled);
        Assert.False(outcome.StoreChanged);
    }

    [Fact]
    public void PendingDisableSurvivesFreshLaunch()
    {
        var outcome = Reconciler.Reconcile(Store(("s", new McpEntry(false, ConfigA))), Servers(("s", ConfigA)), null);
        Assert.False(outcome.Store.Mcps["s"].Enabled);
        Assert.False(outcome.StoreChanged);
    }

    [Fact]
    public void EnabledButMissingLeavesStoreUntouched()
    {
        var s = Store(("gone", new McpEntry(true, ConfigA)), ("also-gone", new McpEntry(true, ConfigB)));
        var outcome = Reconciler.Reconcile(s, Servers());
        Assert.Equal(s, outcome.Store);
        Assert.False(outcome.StoreChanged);
    }

    [Fact]
    public void DisabledAndAbsentIsNormalNoChange()
    {
        var s = Store(("off", new McpEntry(false, ConfigA)));
        var outcome = Reconciler.Reconcile(s, Servers());
        Assert.Equal(s, outcome.Store);
        Assert.False(outcome.StoreChanged);
    }

    [Fact]
    public void IdenticalStateIsNoChange()
    {
        var s = Store(("s", new McpEntry(true, ConfigA)));
        var outcome = Reconciler.Reconcile(s, Servers(("s", ConfigA)));
        Assert.Equal(s, outcome.Store);
        Assert.False(outcome.StoreChanged);
    }

    [Fact]
    public void InputStoreIsNeverMutated()
    {
        var s = MasterStore.Empty();
        Reconciler.Reconcile(s, Servers(("new", ConfigA)));
        Assert.Empty(s.Mcps);
    }

    // adoptSnapshot (Backups ▸ Restore)

    [Fact]
    public void AdoptSnapshotPreservesViewMemoryAndDisablesAbsent()
    {
        var s = Store(
            ("kept", new McpEntry(true, ConfigA, EditView.Json)),
            ("gone", new McpEntry(true, ConfigA)),
            ("off", new McpEntry(false, ConfigB)));
        var outcome = Reconciler.AdoptSnapshot(s, Servers(("kept", ConfigB), ("new", ConfigA)));
        Assert.Equal(new McpEntry(true, ConfigB, EditView.Json), outcome.Store.Mcps["kept"]);
        Assert.Equal(new McpEntry(true, ConfigA), outcome.Store.Mcps["new"]);
        Assert.False(outcome.Store.Mcps["gone"].Enabled);
        Assert.False(outcome.Store.Mcps["off"].Enabled);
        Assert.True(outcome.StoreChanged);
    }

    // the collection folder written back as the token

    private static JsonValue NodeAt(string arg) =>
        JsonValue.Object(("command", JsonValue.String("node")), ("args", JsonValue.Array([JsonValue.String(arg)])));

    private static readonly JsonValue Tokened = NodeAt("${COLLECTION_DIR}/x.js");
    private static readonly JsonValue Expanded = NodeAt("/Users/d/share/x.js");

    [Fact]
    public void AnIngestedConfigTakesTheTokenBack()
    {
        var outcome = Reconciler.Reconcile(MasterStore.Empty(), Servers(("x", Expanded)), directory: "/Users/d/share");
        Assert.Equal(Tokened, outcome.Store.Mcps["x"].Config);
        // With no folder for the collection, the config is taken as it stands.
        Assert.Equal(Expanded, Reconciler.Reconcile(MasterStore.Empty(), Servers(("x", Expanded))).Store.Mcps["x"].Config);
    }

    [Fact]
    public void ARestoredSnapshotKeepsTheStoresTokenAndCollapsesTheRest()
    {
        var s = Store(("x", new McpEntry(true, Tokened)));
        var outcome = Reconciler.AdoptSnapshot(s, Servers(("x", Expanded), ("back", NodeAt("/Users/d/share/y.js"))), "/Users/d/share");
        // The store's copy already renders as the snapshot does.
        Assert.Equal(Tokened, outcome.Store.Mcps["x"].Config);
        Assert.Equal(NodeAt("${COLLECTION_DIR}/y.js"), outcome.Store.Mcps["back"].Config);
        // Restoring what the store already renders changes nothing.
        Assert.False(Reconciler.AdoptSnapshot(s, Servers(("x", Expanded)), "/Users/d/share").StoreChanged);
    }
}

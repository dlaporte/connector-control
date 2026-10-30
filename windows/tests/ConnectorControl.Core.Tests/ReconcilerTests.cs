using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/ReconcilerTests.swift</summary>
public class ReconcilerTests
{
    private static readonly JsonValue ConfigA = JsonValue.Object(("command", JsonValue.String("a")));
    private static readonly JsonValue ConfigB = JsonValue.Object(("command", JsonValue.String("b")));

    private static MasterStore Store(params (string Name, McpEntry Entry)[] mcps) =>
        MasterStore.Single(mcps.Select(m => new KeyValuePair<string, McpEntry>(m.Name, m.Entry)));

    private static Dictionary<string, JsonValue> Servers(params (string Name, JsonValue Config)[] servers) =>
        servers.ToDictionary(s => s.Name, s => s.Config, StringComparer.Ordinal);

    // ingestion - the only file→store flow

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

    // store is the source of truth - the file never edits known entries

    /// <summary>An entry the store no longer holds, changed in Claude's file only in how a character is
    /// composed, is an edit made outside the app, as it is on the Mac: taken in, not left for removal.</summary>
    [Fact]
    public void AnEditOnlyInHowACharacterIsComposedIsExternal()
    {
        var composed = JsonValue.Object(("command", JsonValue.String("caf\u00E9")));
        var decomposed = JsonValue.Object(("command", JsonValue.String("cafe\u0301")));
        var outcome = Reconciler.Reconcile(MasterStore.Empty(), Servers(("gone", decomposed)), Servers(("gone", composed)));
        Assert.Equal(decomposed, outcome.Store.Mcps["gone"].Config);
        Assert.True(outcome.StoreChanged);
    }

    /// <summary>A name in Claude's file spelled another way, é decomposed where the store composes it, is the
    /// store's own connector, as on the Mac: nothing is taken in.</summary>
    [Fact]
    public void ANameSpelledAnotherWayIsTheStoresConnector()
    {
        var outcome = Reconciler.Reconcile(Store(("caf\u00E9", new McpEntry(true, ConfigA))), Servers(("cafe\u0301", ConfigB)));
        Assert.Single(outcome.Store.Mcps);
        Assert.Equal(ConfigA, outcome.Store.Mcps["caf\u00E9"].Config);
        Assert.False(outcome.StoreChanged);
    }

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

    // ingestion into another collection

    [Fact]
    public void AnAdditionCanLandInACollectionOtherThanTheActiveOne()
    {
        var s = Store(("team", new McpEntry(false, ConfigB)));
        s.Collections["Personal"] = new Collection();
        var outcome = Reconciler.Reconcile(s, Servers(("new", ConfigA), ("team", ConfigA)), target: "Personal");
        Assert.Equal(new McpEntry(true, ConfigA), outcome.Store.Collections["Personal"].Mcps["new"]);
        // The active collection is left exactly as it was, and a name it holds is not new.
        Assert.False(outcome.Store.Mcps.ContainsKey("new"));
        Assert.Equal(ConfigB, outcome.Store.Mcps["team"].Config);
        Assert.False(outcome.Store.Collections["Personal"].Mcps.ContainsKey("team"));
        Assert.Equal(["new"], outcome.Ingested);
        Assert.True(outcome.StoreChanged);
    }

    [Fact]
    public void AnAdditionCreatesTheCollectionItLandsIn()
    {
        var outcome = Reconciler.Reconcile(Store(), Servers(("new", ConfigA)), target: "Default 2");
        Assert.Equal(new McpEntry(true, ConfigA), outcome.Store.Collections["Default 2"].Mcps["new"]);
        Assert.Equal("Default", outcome.Store.ActiveCollection);
        // Nothing to take in creates nothing.
        Assert.False(Reconciler.Reconcile(Store(), Servers(), target: "Default 2").Store.Collections.ContainsKey("Default 2"));
    }

    [Fact]
    public void AnAdditionNeverOverwritesWhatTheOtherCollectionHolds()
    {
        var s = Store();
        s.Collections["Personal"] = new Collection([
            new KeyValuePair<string, McpEntry>("same", new McpEntry(false, ConfigA)),
            new KeyValuePair<string, McpEntry>("other", new McpEntry(false, ConfigB)),
        ]);
        var outcome = Reconciler.Reconcile(s, Servers(("same", ConfigA), ("other", ConfigA)), target: "Personal");
        // The same connector is already there.
        Assert.Equal(new McpEntry(false, ConfigA), outcome.Store.Collections["Personal"].Mcps["same"]);
        Assert.Equal(ConfigB, outcome.Store.Collections["Personal"].Mcps["other"].Config);
        // A different one under a taken name lands beside it.
        Assert.Equal(new McpEntry(true, ConfigA), outcome.Store.Collections["Personal"].Mcps["other 2"]);
        Assert.Equal(["other 2", "same"], outcome.Ingested);
    }

    /// <summary>C#-only: MasterStore is a class here, so Reconcile could edit its argument; a Swift
    /// struct is copied on the way in.</summary>
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
    public void ARestoredSnapshotKeepsTheStoresTokenOnlyWhereItRendersTheSame()
    {
        var s = Store(("x", new McpEntry(true, Tokened)), ("edited", new McpEntry(true, Tokened)));
        var edit = NodeAt("/Users/d/share/y.js");
        var outcome = Reconciler.AdoptSnapshot(s, Servers(("x", Expanded), ("edited", edit), ("back", Expanded)), "/Users/d/share");
        // The store's copy already renders as the snapshot does.
        Assert.Equal(Tokened, outcome.Store.Mcps["x"].Config);
        // A genuine edit is adopted as written, and a name the store does not hold has nothing to keep.
        Assert.Equal(edit, outcome.Store.Mcps["edited"].Config);
        Assert.Equal(Expanded, outcome.Store.Mcps["back"].Config);
        // Restoring what the store already renders changes nothing.
        Assert.False(Reconciler.AdoptSnapshot(Store(("x", new McpEntry(true, Tokened))), Servers(("x", Expanded)), "/Users/d/share").StoreChanged);
        // With no folder published from here, the snapshot is adopted as written.
        Assert.Equal(Expanded, Reconciler.AdoptSnapshot(s, Servers(("x", Expanded))).Store.Mcps["x"].Config);
    }

    // MARK: the reserved name

    private static readonly JsonValue ReservedEntry = JsonValue.Object(
        ("command", JsonValue.String("/x/connector-control")), ("args", JsonValue.Array([JsonValue.String("mcp")])));

    [Fact]
    public void TheReservedEntryIsNeverIngested()
    {
        var outcome = Reconciler.Reconcile(MasterStore.Empty(), Servers(("connector-control", ReservedEntry), ("new", ConfigA)));
        Assert.False(outcome.Store.Mcps.ContainsKey("connector-control"));
        Assert.True(outcome.Store.Mcps.ContainsKey("new"));
        Assert.Equal(["new"], outcome.Ingested);
    }

    [Fact]
    public void ARestoredSnapshotNeverAdoptsTheReservedEntry()
    {
        var outcome = Reconciler.AdoptSnapshot(Store(("a", new McpEntry(true, ConfigA))),
                                               Servers(("connector-control", ReservedEntry), ("a", ConfigA)));
        Assert.False(outcome.Store.Mcps.ContainsKey("connector-control"));
        Assert.True(outcome.Store.Mcps["a"].Enabled);
        Assert.False(outcome.StoreChanged);
    }
}

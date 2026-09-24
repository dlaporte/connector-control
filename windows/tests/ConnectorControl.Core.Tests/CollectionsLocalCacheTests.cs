using ConnectorControl.Core;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/CollectionsLocalCacheTests.swift</summary>
public sealed class CollectionsLocalCacheTests : IDisposable
{
    private readonly TempDir dir = new("cache");

    public void Dispose() => dir.Dispose();

    private static CollectionsLocalCache Sample => new(
        new Dictionary<string, CollectionsLocalCache.SyncedBinding>
        {
            ["Data team"] = new("/Users/d/Acme/mcp/data-team.json", "sha256:00",
                                new Dictionary<string, string>(StringComparer.Ordinal) { ["ledger"] = "reason" }),
        },
        new Dictionary<string, CollectionsLocalCache.PublishBinding>
        {
            ["Consulting"] = new("/Users/d/Acme/mcp", null, null, null, ["/Users/d/Acme/mcp"]),
        });

    [Fact]
    public void RoundTripThroughDisk()
    {
        var path = dir.File("collections-local.json");
        Assert.True(Sample.Save(path).Protected);
        Assert.Equal(Sample, CollectionsLocalCache.Load(path));
        Assert.Equal(new CollectionsLocalCache([], []), CollectionsLocalCache.Load(dir.File("missing.json")));
    }

    /// <summary>
    /// C#-only: a Swift struct copies on assignment, so there is no <c>with</c> to hand it a
    /// dictionary that compares keys some other way.
    /// </summary>
    [Fact]
    public void AWithKeepsKeysOrdinal()
    {
        var folded = new Dictionary<string, CollectionsLocalCache.SyncedBinding>(StringComparer.OrdinalIgnoreCase)
        {
            ["Team"] = new("/t.json", null, null),
        };
        var cache = Sample with { Synced = folded };
        Assert.False(cache.Synced.ContainsKey("team"));
        var entry = CollectionsFile.Entry.Local with
        {
            Provenance = new Dictionary<string, CollectionsFile.Provenance>(StringComparer.OrdinalIgnoreCase)
            {
                ["dbt"] = new("Data team", null, "2026-09-21"),
            },
        };
        Assert.False(entry.Provenance.ContainsKey("DBT"));
    }

    [Fact]
    public void ReconcilePrunesBindingsWhoseCollectionChangedKind()
    {
        var file = new CollectionsFile(new Dictionary<string, CollectionsFile.Entry>
        {
            ["Data team"] = new(CollectionKind.Local, null, null, null, null, null, null),
        });
        var pruned = Sample.Reconciled(file);
        Assert.Empty(pruned.Synced);       // Data team is local now, so its source binding is gone
        Assert.Empty(pruned.Published);    // Consulting has no publish record in the sidecar
    }

    /// <summary>The two bindings the sidecar still vouches for survive reconciliation untouched.</summary>
    [Fact]
    public void ReconcileKeepsBindingsTheSidecarStillVouchesFor()
    {
        var file = new CollectionsFile(new Dictionary<string, CollectionsFile.Entry>
        {
            ["Data team"] = new(CollectionKind.Synced, "data-team.json"),
            ["Consulting"] = new(CollectionKind.Local, publish: new CollectionsFile.PublishRecord("consulting", "0c9b7d1e", PublishIntent.None)),
        });
        Assert.Equal(Sample, Sample.Reconciled(file));
    }

    /// <summary>
    /// The list of marked paths round-trips sorted, is left out while empty, and a cache written
    /// before it was kept loads with an empty one.
    /// </summary>
    [Fact]
    public void MarkedValuesRoundTripAndAreEmptyInAnOlderCache()
    {
        var marked = new CollectionsLocalCache([], new Dictionary<string, CollectionsLocalCache.PublishBinding>
        {
            ["Consulting"] = new("/Users/d/Acme/mcp", "sha256:01", ["/Users/d/ledger.js", "/Users/d/b.js"], null,
                                 ["/Users/d/Acme/mcp"]),
        });
        Assert.Equal(marked, CollectionsLocalCache.Decode(marked.Encode()));
        Assert.Equal(
            JsonValue.Array([JsonValue.String("/Users/d/b.js"), JsonValue.String("/Users/d/ledger.js")]),
            marked.Encode().ValueAt(new JsonPointer(["published", "Consulting", "markedValues"])));
        Assert.Null(Sample.Encode().ValueAt(new JsonPointer(["published", "Consulting", "markedValues"])));

        var older = JsonValue.Parse("""
            {"version": 1, "synced": {}, "published": {"Consulting": {"folder": "/Users/d/Acme/mcp"}}}
            """);
        Assert.Empty(CollectionsLocalCache.Decode(older).Published["Consulting"].MarkedValues);
    }

    [Fact]
    public void ReleasedValuesAndTheLastAppliedCollectionRoundTripAndAreAbsentInAnOlderCache()
    {
        var cache = new CollectionsLocalCache([], new Dictionary<string, CollectionsLocalCache.PublishBinding>
        {
            ["Consulting"] = new("/Users/d/Acme/mcp", null, ["/a"], ["/b"], ["/Users/d/old", "/Users/d/Acme/mcp"]),
        }, null, "Consulting");
        Assert.Equal(cache, CollectionsLocalCache.Decode(cache.Encode()));
        // Which collection Claude's file holds is not a binding to prune.
        Assert.Equal("Consulting", cache.Reconciled(new CollectionsFile([])).LastAppliedCollection);
        var older = CollectionsLocalCache.Decode(Sample.Encode());
        Assert.Null(older.LastAppliedCollection);
        Assert.Empty(older.Published["Consulting"].ReleasedValues);
        Assert.NotEqual(cache, cache with { LastAppliedCollection = "Other" });
    }

    [Fact]
    public void WhatAStoppedPublishLeftBehindRoundTripsAndOutlivesThePrune()
    {
        var cache = new CollectionsLocalCache([], [], new Dictionary<string, CollectionsLocalCache.KeptRecord>
        {
            ["Consulting"] = new(["/a"], ["/b"], ["/Users/d/old"], null),
            ["Empty"] = new(null, null, null, null),
        });
        var decoded = CollectionsLocalCache.Decode(cache.Encode());
        Assert.Equal(cache.Kept["Consulting"], decoded.Kept["Consulting"]);
        Assert.False(decoded.Kept.ContainsKey("Empty"));   // a record with nothing to say is not written
        // No sidecar vouches for it, and it is kept all the same.
        Assert.Equal(decoded.Kept, decoded.Reconciled(new CollectionsFile([])).Kept);
        var older = JsonValue.Parse("""
            {"version": 1, "synced": {}, "published": {"Consulting": {"folder": "/Users/d/Acme/mcp"}}}
            """);
        Assert.Empty(CollectionsLocalCache.Decode(older).Kept);
        // A binding knows it publishes into the folder it names.
        Assert.Equal(["/Users/d/Acme/mcp"], CollectionsLocalCache.Decode(older).Published["Consulting"].PublishedFolders);
    }

    /// <summary>
    /// A publish binding the sidecar no longer vouches for is a collection deleted, or stopped, on
    /// another machine. What it kept back outlives it, exactly as Stop Publishing here leaves it.
    /// </summary>
    [Fact]
    public void ReconcileKeepsWhatADroppedPublishBindingKeptBack()
    {
        var cache = new CollectionsLocalCache(
            [],
            new Dictionary<string, CollectionsLocalCache.PublishBinding>
            {
                ["Consulting"] = new("/Users/d/new", null, ["/a"], ["/b"], ["/Users/d/old"], "0c9b7d1e"),
            },
            new Dictionary<string, CollectionsLocalCache.KeptRecord>
            {
                ["Consulting"] = new(["/earlier"], null, null, null),
            });
        var pruned = cache.Reconciled(new CollectionsFile([]));
        Assert.Empty(pruned.Published);   // the sidecar no longer vouches for it
        // The binding's lists, its folder among them, merged with what was already remembered.
        Assert.Equal(new CollectionsLocalCache.KeptRecord(["/a", "/earlier"], ["/b"], ["/Users/d/old", "/Users/d/new"], null, "0c9b7d1e"),
                     pruned.Kept["Consulting"]);
        // And folding it again changes nothing.
        Assert.Equal(pruned, pruned.Reconciled(new CollectionsFile([])));
    }

    /// <summary>
    /// The origin a binding publishes under, and the names the last apply wrote, round-trip; a cache
    /// written before either was kept has neither, and an apply that rendered nothing records an
    /// empty list, which is not the same as no record at all.
    /// </summary>
    [Fact]
    public void TheOriginAndTheNamesTheLastApplyWroteRoundTrip()
    {
        var cache = new CollectionsLocalCache(
            [],
            new Dictionary<string, CollectionsLocalCache.PublishBinding>
            {
                ["Consulting"] = new("/Users/d/Acme/mcp", null, null, null, ["/Users/d/Acme/mcp"], "0c9b7d1e"),
            },
            new Dictionary<string, CollectionsLocalCache.KeptRecord>
            {
                ["Gone"] = new(null, null, ["/Users/d/old"], null, "5f2a"),
            },
            "Consulting",
            ["ledger", "scoutbook"]);
        Assert.Equal(cache, CollectionsLocalCache.Decode(cache.Encode()));
        var older = CollectionsLocalCache.Decode(Sample.Encode());
        Assert.Null(older.LastAppliedNames);   // a cache written before they were recorded names nothing
        Assert.Null(older.Published["Consulting"].Origin);
        // An apply that rendered nothing is a record of nothing, not the absence of one.
        var empty = cache with { LastAppliedNames = new HashSet<string>(StringComparer.Ordinal) };
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlySet<string>>(CollectionsLocalCache.Decode(empty.Encode()).LastAppliedNames));
    }

    /// <summary>
    /// The folders of the collections that bore a record's name and left round-trip beside its
    /// own, and are enough on their own for the record to be written; a record written before they
    /// were kept apart reads with none.
    /// </summary>
    [Fact]
    public void DepartedFoldersRoundTripAndAreEmptyInAnOlderCache()
    {
        var cache = new CollectionsLocalCache([], [], new Dictionary<string, CollectionsLocalCache.KeptRecord>
        {
            ["Team"] = new(null, null, null, ["/Users/d/old"], "5f2a"),
        });
        // A record holding only what departed collections left has something to say.
        Assert.Equal(cache.Kept["Team"], CollectionsLocalCache.Decode(cache.Encode()).Kept["Team"]);
        Assert.Equal(JsonValue.Array([JsonValue.String("/Users/d/old")]),
                     cache.Encode().ValueAt(new JsonPointer(["kept", "Team", "departedFolders"])));
        var older = JsonValue.Parse("""
            {"version": 1, "synced": {}, "published": {}, "kept": {"Team": {"publishedFolders": ["/Users/d/old"]}}}
            """);
        Assert.Empty(CollectionsLocalCache.Decode(older).Kept["Team"].DepartedFolders);
    }

    /// <summary>
    /// An earlier record's folders are the binding's own only where the two published under one
    /// origin. A record of another origin, or of none — a collection that left the store — belongs
    /// to another collection, and its folders are kept apart as departed so the next publish does
    /// not take them as its own.
    /// </summary>
    [Fact]
    public void RememberingKeepsAnotherCollectionsFoldersApartFromTheBindingsOwn()
    {
        var binding = new CollectionsLocalCache.PublishBinding("/Users/d/new", null, null, null, ["/Users/d/new"], "0c9b7d1e");
        var departed = new CollectionsLocalCache.KeptRecord(null, null, ["/Users/d/old"], ["/Users/d/older"]);
        // A record with no origin belongs to no collection here.
        Assert.Equal(new CollectionsLocalCache.KeptRecord(null, null, ["/Users/d/new"], ["/Users/d/old", "/Users/d/older"], "0c9b7d1e"),
                     CollectionsLocalCache.KeptRecord.Remembering(binding, departed));
        var another = new CollectionsLocalCache.KeptRecord(null, null, ["/Users/d/old"], null, "5f2a");
        // And one of another origin belongs to another collection.
        Assert.Equal(new CollectionsLocalCache.KeptRecord(null, null, ["/Users/d/new"], ["/Users/d/old"], "0c9b7d1e"),
                     CollectionsLocalCache.KeptRecord.Remembering(binding, another));
    }

    /// <summary>
    /// A record of the binding's own origin is the same collection's, stopped before: its folders
    /// merge into the binding's own, and what it held as departed stays departed. A binding and a
    /// record written before origins were kept, with none on either side, are read the same way.
    /// </summary>
    [Fact]
    public void RememberingMergesTheFoldersOfARecordOfTheSameOrigin()
    {
        var binding = new CollectionsLocalCache.PublishBinding("/Users/d/new", null, null, null, ["/Users/d/new"], "0c9b7d1e");
        var own = new CollectionsLocalCache.KeptRecord(null, null, ["/Users/d/old"], ["/Users/d/older"], "0c9b7d1e");
        Assert.Equal(new CollectionsLocalCache.KeptRecord(null, null, ["/Users/d/old", "/Users/d/new"], ["/Users/d/older"], "0c9b7d1e"),
                     CollectionsLocalCache.KeptRecord.Remembering(binding, own));
        var legacy = new CollectionsLocalCache.PublishBinding("/Users/d/new", null);
        Assert.Equal(new CollectionsLocalCache.KeptRecord(null, null, ["/Users/d/old", "/Users/d/new"], null),
                     CollectionsLocalCache.KeptRecord.Remembering(legacy, new(null, null, ["/Users/d/old"], null)));
    }

    /// <summary>
    /// A collection that never published moves no record, so a rename onto a name a departed
    /// collection left a record under leaves that record as it found it, belonging to none: the
    /// same reading a collection made with the name gets.
    /// </summary>
    [Fact]
    public void RenamedWithNothingMovingLeavesTheDisplacedRecordAsItIs()
    {
        var displaced = new CollectionsLocalCache.KeptRecord(["/a"], null, ["/Users/d/old"], ["/Users/d/older"]);
        Assert.Equal(displaced, CollectionsLocalCache.KeptRecord.Renamed(null, displaced));
    }

    /// <summary>
    /// A live collection's name is refused, so a record displaced by a rename is a departed
    /// collection's: its paths are inherited, as a re-used name inherits them, and its folders, own
    /// and departed alike, are departed to the collection now bearing the name, whose own folders
    /// and origin the merged record keeps.
    /// </summary>
    [Fact]
    public void RenamedFilesTheDisplacedRecordsFoldersAsDeparted()
    {
        var moving = new CollectionsLocalCache.KeptRecord(["/c"], ["/d"], ["/Users/d/squad"], ["/Users/d/gone"], "0c9b7d1e");
        var displaced = new CollectionsLocalCache.KeptRecord(["/a"], ["/b"], ["/Users/d/old"], ["/Users/d/older"]);
        Assert.Equal(
            new CollectionsLocalCache.KeptRecord(["/a", "/c"], ["/b", "/d"], ["/Users/d/squad"],
                                                 ["/Users/d/gone", "/Users/d/old", "/Users/d/older"], "0c9b7d1e"),
            CollectionsLocalCache.KeptRecord.Renamed(moving, displaced));
    }

    [Fact]
    public void AnUnknownVersionDecodesAsMalformed()
    {
        Assert.Throws<CollectionsFileException>(() => CollectionsLocalCache.Decode(JsonValue.Object(
            ("version", JsonValue.Int(9)), ("synced", JsonValue.Object()), ("published", JsonValue.Object()))));
    }
}

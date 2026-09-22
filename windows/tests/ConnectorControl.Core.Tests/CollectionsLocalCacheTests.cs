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
            ["Consulting"] = new(["/a"], ["/b"], ["/Users/d/old"]),
            ["Empty"] = new(),
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

    [Fact]
    public void AnUnknownVersionDecodesAsMalformed()
    {
        Assert.Throws<CollectionsFileException>(() => CollectionsLocalCache.Decode(JsonValue.Object(
            ("version", JsonValue.Int(9)), ("synced", JsonValue.Object()), ("published", JsonValue.Object()))));
    }
}

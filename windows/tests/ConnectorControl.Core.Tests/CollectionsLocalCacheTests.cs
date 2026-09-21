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
            ["Consulting"] = new("/Users/d/Acme/mcp", null),
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

    [Fact]
    public void AnUnknownVersionDecodesAsMalformed()
    {
        Assert.Throws<CollectionsFileException>(() => CollectionsLocalCache.Decode(JsonValue.Object(
            ("version", JsonValue.Int(9)), ("synced", JsonValue.Object()), ("published", JsonValue.Object()))));
    }
}

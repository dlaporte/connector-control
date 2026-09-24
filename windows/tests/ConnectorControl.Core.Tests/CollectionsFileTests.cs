using System.Runtime.Versioning;
using ConnectorControl.Core;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/CollectionsFileTests.swift</summary>
public sealed class CollectionsFileTests : IDisposable
{
    private readonly TempDir dir = new("collections");

    public void Dispose() => dir.Dispose();

    private static CollectionsFile Sample => new(new Dictionary<string, CollectionsFile.Entry>
    {
        ["Personal"] = new(
            CollectionKind.Local, null, null, null, null, null,
            new Dictionary<string, CollectionsFile.Provenance>
            {
                ["dbt"] = new("Data team", "Acme Data Platform", "2026-09-21"),
            }),
        ["Data team"] = new(
            CollectionKind.Synced, "data-team.json", "../mcp/data-team.json", "6f1c4a2e-1b8d-4b0e-9f0a-3c2d7e8a91e2",
            new Dictionary<string, IReadOnlyDictionary<string, CollectionsFile.Need>>
            {
                ["dbt"] = new Dictionary<string, CollectionsFile.Need>
                {
                    ["DBT_TOKEN"] = new("cloud.getdbt.com ▸ Account ▸ API tokens", new JsonPointer(["env", "DBT_TOKEN"])),
                    ["server_path"] = new("your ledger clone, then dist/index.js", new JsonPointer(["args", "1"])),
                },
            },
            null,
            null),
        ["Consulting"] = new(
            CollectionKind.Local, null, null, null, null,
            new CollectionsFile.PublishRecord("consulting", "0c9b7d1e-5a3f-4f2c-8e6d-2b1a9c8d7e6f", new PublishIntent(
                new Dictionary<string, IReadOnlySet<string>> { ["tax"] = new HashSet<string>(StringComparer.Ordinal) { "LOG_LEVEL" } },
                new Dictionary<string, IReadOnlyDictionary<JsonPointer, PublishIntent.PathMark>>
                {
                    ["ledger"] = new Dictionary<JsonPointer, PublishIntent.PathMark>
                    {
                        [new JsonPointer(["args", "1"])] = new("server_path", "your ledger clone, then dist/index.js",
                            "/Users/you/ledger/dist/index.js"),
                    },
                },
                new Dictionary<string, IReadOnlyDictionary<string, string>>
                {
                    ["tax"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["ANTHROPIC_API_KEY"] = "console.anthropic.com ▸ API keys" },
                })),
            null),
    });

    [Fact]
    public void EncodeDecodeRoundTripAndGolden()
    {
        Assert.Equal(Sample, CollectionsFile.Decode(Sample.Encode()));
        // Written once from the Mac's Foundation, then compared byte for byte there and here,
        // so both platforms write the same sidecar. This side never regenerates it.
        var expected = File.ReadAllBytes(Fixtures.Path("collections.json"));
        Assert.Equal(expected, Sample.Encode().Serialize());
        Assert.Equal(Sample, CollectionsFile.Decode(JsonValue.Parse(expected)));
    }

    [Fact]
    public void KindDefaultsToLocal()
    {
        Assert.Equal(CollectionKind.Synced, Sample.KindOf("Data team"));
        Assert.Equal(CollectionKind.Local, Sample.KindOf("Personal"));
        Assert.Equal(CollectionKind.Local, Sample.KindOf("Never heard of it"));
    }

    [Fact]
    public void LoadTreatsMissingAndCorruptAsEmpty()
    {
        var path = dir.File("collections.json");
        Assert.Equal(new CollectionsFile([]), CollectionsFile.Load(path));
        File.WriteAllText(path, "{not json");
        Assert.Equal(new CollectionsFile([]), CollectionsFile.Load(path));
        Assert.True(File.Exists(path), "load never moves a file aside");
    }

    [Fact]
    public void LoadIfReadableTellsAnUnreadableFileFromAnEmptyOne()
    {
        var path = dir.File("collections.json");
        Assert.Equal(new CollectionsFile([]), CollectionsFile.LoadIfReadable(path));   // missing is empty
        new CollectionsFile([]).Save(path);
        Assert.Equal(new CollectionsFile([]), CollectionsFile.LoadIfReadable(path));   // no entries is empty
        File.WriteAllText(path, "{not json");
        Assert.Null(CollectionsFile.LoadIfReadable(path));   // a file that cannot be parsed is unreadable
        File.WriteAllText(path, "{\"version\":2,\"collections\":{}}");
        Assert.Null(CollectionsFile.LoadIfReadable(path));   // a file that cannot be decoded is unreadable
        File.WriteAllBytes(path, []);
        Assert.Null(CollectionsFile.LoadIfReadable(path));   // a file truncated to nothing is unreadable
        Assert.True(File.Exists(path), "load never moves a file aside");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void SaveIsOwnerOnlyAndReloads()
    {
        var path = dir.File("collections.json");
        Assert.True(Sample.Save(path).Protected);
        Assert.Equal(Sample, CollectionsFile.Load(path));
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.True(OwnerOnlyAcl.IsOwnerOnly(path));
    }

    [Fact]
    public void ReconcileDropsNamesTheStoreNoLongerHas()
    {
        var store = new MasterStore(2, "Personal", new Dictionary<string, Collection>
        {
            ["Personal"] = new(),
            ["Consulting"] = new(),
        });
        var reconciled = Sample.Reconciled(store);
        Assert.Equal(["Consulting", "Personal"], reconciled.Collections.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void AnUnknownVersionDecodesAsMalformed()
    {
        Assert.Throws<CollectionsFileException>(() => CollectionsFile.Decode(
            JsonValue.Object(("version", JsonValue.Int(9)), ("collections", JsonValue.Object()))));
    }

    /// <summary>
    /// No release wrote a path mark without the value it was made on, but a build from before
    /// values were kept may have: such a mark loads as the pointer-only mark it was.
    /// </summary>
    [Fact]
    public void APathMarkWithNoValueStillLoads()
    {
        var json = JsonValue.Parse("""
            {"version": 1, "collections": {"Consulting": {"kind": "local", "publish": {
              "slug": "consulting", "origin": "o", "shareValues": {}, "hints": {},
              "paths": {"ledger": {"/args/1": {"name": "server_path", "hint": null}}}}}}}
            """);
        var marks = CollectionsFile.Decode(json).Collections["Consulting"].Publish!.Intent.PathMarks["ledger"];
        Assert.Equal(new PublishIntent.PathMark("server_path", null, null), Assert.Single(marks).Value);
        Assert.Equal(new JsonPointer(["args", "1"]), Assert.Single(marks).Key);
    }

    /// <summary>A local collection with nothing to say is absent from the file, not written as an empty entry: the sidecar only ever records what the master list cannot.</summary>
    [Fact]
    public void ALocalCollectionWithNothingToSayIsNotWritten()
    {
        var file = new CollectionsFile(new Dictionary<string, CollectionsFile.Entry>
        {
            ["Personal"] = CollectionsFile.Entry.Local,
            ["Consulting"] = Sample.Collections["Consulting"],
        });
        Assert.Null(file.Encode().ValueAt(new JsonPointer(["collections", "Personal"])));
        Assert.Equal(["Consulting"], CollectionsFile.Decode(file.Encode()).Collections.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(CollectionKind.Local, file.KindOf("Personal"));
    }
}

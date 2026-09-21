using ConnectorControl.Core;

namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/CollectionDiffTests.swift</summary>
public class CollectionDiffTests
{
    private static RenderedCollection Rendered(IEnumerable<(string Name, JsonValue Config, IReadOnlyDictionary<string, RenderedNeed> Needs)> connectors) =>
        new(
            connectors.ToDictionary(c => c.Name, c => new RenderedConnector(c.Config, c.Needs, null), StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

    private static readonly JsonValue DbtRendered = JsonValue.Object(
        ("command", JsonValue.String("npx")),
        ("args", JsonValue.Array([JsonValue.String("-y"), JsonValue.String("@dbt/mcp")])),
        ("env", JsonValue.Object(("DBT_TOKEN", JsonValue.String("${CC_NEEDS:DBT_TOKEN}")))));

    private static readonly JsonValue DbtFilled = JsonValue.Object(
        ("command", JsonValue.String("npx")),
        ("args", JsonValue.Array([JsonValue.String("-y"), JsonValue.String("@dbt/mcp")])),
        ("env", JsonValue.Object(("DBT_TOKEN", JsonValue.String("tok-123")))));

    private static IReadOnlyDictionary<string, RenderedNeed> TokenNeed =>
        new Dictionary<string, RenderedNeed> { ["DBT_TOKEN"] = new(null, new JsonPointer(["env", "DBT_TOKEN"])) };

    [Fact]
    public void AFilledMarkerIsNotAChange()
    {
        var diff = CollectionDiff.Pending(
            Rendered([("dbt", DbtRendered, TokenNeed)]),
            new Dictionary<string, McpEntry> { ["dbt"] = new(true, DbtFilled) });
        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void AddedRemovedAndChangedAreNamedAndSorted()
    {
        var current = new Dictionary<string, McpEntry>
        {
            ["dbt"] = new(false, DbtFilled),
            ["confluence"] = new(JsonValue.Object(("command", JsonValue.String("x")))),
        };
        var changedDbt = DbtRendered.Replacing(new JsonPointer(["args", "1"]), JsonValue.String("@dbt/mcp@2"))!;
        var diff = CollectionDiff.Pending(
            Rendered([("dbt", changedDbt, TokenNeed), ("datadog", JsonValue.Object(("command", JsonValue.String("d"))), new Dictionary<string, RenderedNeed>())]),
            current);
        Assert.Equal(["datadog"], diff.Added);
        Assert.Equal(["confluence"], diff.Removed);
        Assert.Equal(["dbt"], diff.Changed);
        Assert.Equal("adds datadog; removes confluence; changes dbt", diff.Summary());
    }

    [Fact]
    public void ApplyCarriesAFilledValueToTheMarkersNewPlace()
    {
        // The author inserted an argument, so the marker moved from /args/0 to /args/1.
        var previous = new Dictionary<string, IReadOnlyDictionary<string, CollectionsFile.Need>>
        {
            ["ledger"] = new Dictionary<string, CollectionsFile.Need> { ["server_path"] = new(null, new JsonPointer(["args", "0"])) },
        };
        var current = new Dictionary<string, McpEntry>
        {
            ["ledger"] = new(true, JsonValue.Object(
                ("command", JsonValue.String("node")),
                ("args", JsonValue.Array([JsonValue.String("/Users/d/ledger/index.js")])))),
        };
        var newConfig = JsonValue.Object(
            ("command", JsonValue.String("node")),
            ("args", JsonValue.Array([JsonValue.String("--inspect"), JsonValue.String("${CC_NEEDS:server_path}")])));
        var result = CollectionApply.Apply(
            Rendered([("ledger", newConfig, new Dictionary<string, RenderedNeed> { ["server_path"] = new("h", new JsonPointer(["args", "1"])) })]),
            current, previous);
        Assert.Equal(JsonValue.String("/Users/d/ledger/index.js"), result.Entries["ledger"].Config.ValueAt(new JsonPointer(["args", "1"])));
        Assert.True(result.Entries["ledger"].Enabled);
        Assert.Equal(new CollectionsFile.Need("h", new JsonPointer(["args", "1"])), result.Needs["ledger"]["server_path"]);
    }

    [Fact]
    public void ApplyLeavesAnUnfilledMarkerAndDisablesNewConnectors()
    {
        var result = CollectionApply.Apply(
            Rendered([("dbt", DbtRendered, TokenNeed)]),
            new Dictionary<string, McpEntry>(),
            new Dictionary<string, IReadOnlyDictionary<string, CollectionsFile.Need>>());
        Assert.Equal(DbtRendered, result.Entries["dbt"].Config);
        Assert.False(result.Entries["dbt"].Enabled);
    }

    [Fact]
    public void ApplyDropsConnectorsTheSourceRemoved()
    {
        var result = CollectionApply.Apply(
            Rendered([]),
            new Dictionary<string, McpEntry> { ["gone"] = new(JsonValue.Object()) },
            new Dictionary<string, IReadOnlyDictionary<string, CollectionsFile.Need>>());
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void PendingIgnoresEnabledAndExcluded()
    {
        var r = Rendered([("dbt", DbtRendered, TokenNeed)]);
        r = r with { Excluded = new Dictionary<string, string> { ["ledger"] = "reason" } };
        var diff = CollectionDiff.Pending(r, new Dictionary<string, McpEntry> { ["dbt"] = new(false, DbtFilled) });
        Assert.True(diff.IsEmpty);
    }

    // Two marker names can render into the same leaf, e.g. "${CC_NEEDS:a}-${CC_NEEDS:b}"; apply
    // must not let dictionary iteration order (unspecified on both platforms) decide which
    // filled value survives.
    [Fact]
    public void TwoMarkersInOneLeafCarryTheSortedFirstFilledValue()
    {
        var previous = new Dictionary<string, IReadOnlyDictionary<string, CollectionsFile.Need>>
        {
            ["multi"] = new Dictionary<string, CollectionsFile.Need>
            {
                ["a"] = new(null, new JsonPointer(["args", "0"])),
                ["b"] = new(null, new JsonPointer(["args", "1"])),
            },
        };
        var current = new Dictionary<string, McpEntry>
        {
            ["multi"] = new(true, JsonValue.Object(
                ("command", JsonValue.String("c")),
                ("args", JsonValue.Array([JsonValue.String("alpha"), JsonValue.String("beta")])))),
        };
        var newConfig = JsonValue.Object(
            ("command", JsonValue.String("c")),
            ("args", JsonValue.Array([JsonValue.String("${CC_NEEDS:a}-${CC_NEEDS:b}")])));
        var needs = new Dictionary<string, RenderedNeed>
        {
            ["a"] = new(null, new JsonPointer(["args", "0"])),
            ["b"] = new(null, new JsonPointer(["args", "0"])),
        };
        for (var i = 0; i < 10; i++)
        {
            var result = CollectionApply.Apply(Rendered([("multi", newConfig, needs)]), current, previous);
            Assert.Equal(JsonValue.String("alpha"), result.Entries["multi"].Config.ValueAt(new JsonPointer(["args", "0"])));
        }
    }

    [Fact]
    public void AMissingOrNonStringCurrentLeafIsAChange()
    {
        var renderedConfig = JsonValue.Object(
            ("command", JsonValue.String("c")),
            ("env", JsonValue.Object(("X", JsonValue.String("${CC_NEEDS:X}")))));
        var needs = new Dictionary<string, RenderedNeed> { ["X"] = new(null, new JsonPointer(["env", "X"])) };

        var noEnv = CollectionDiff.Pending(
            Rendered([("srv", renderedConfig, needs)]),
            new Dictionary<string, McpEntry> { ["srv"] = new(JsonValue.Object(("command", JsonValue.String("c")))) });
        Assert.Equal(["srv"], noEnv.Changed);

        var numericEnv = CollectionDiff.Pending(
            Rendered([("srv", renderedConfig, needs)]),
            new Dictionary<string, McpEntry>
            {
                ["srv"] = new(JsonValue.Object(("command", JsonValue.String("c")), ("env", JsonValue.Object(("X", JsonValue.Int(5)))))),
            });
        Assert.Equal(["srv"], numericEnv.Changed);
    }

    [Fact]
    public void AStalePreviousPointerLeavesTheMarker()
    {
        var previous = new Dictionary<string, IReadOnlyDictionary<string, CollectionsFile.Need>>
        {
            ["ledger"] = new Dictionary<string, CollectionsFile.Need> { ["server_path"] = new(null, new JsonPointer(["args", "3"])) },
        };
        var current = new Dictionary<string, McpEntry>
        {
            ["ledger"] = new(true, JsonValue.Object(
                ("command", JsonValue.String("node")),
                ("args", JsonValue.Array([JsonValue.String("/Users/d/ledger/index.js")])))),
        };
        var newConfig = JsonValue.Object(
            ("command", JsonValue.String("node")),
            ("args", JsonValue.Array([JsonValue.String("${CC_NEEDS:server_path}")])));
        var result = CollectionApply.Apply(
            Rendered([("ledger", newConfig, new Dictionary<string, RenderedNeed> { ["server_path"] = new(null, new JsonPointer(["args", "0"])) })]),
            current, previous);
        Assert.Equal(JsonValue.String("${CC_NEEDS:server_path}"), result.Entries["ledger"].Config.ValueAt(new JsonPointer(["args", "0"])));
    }

    [Fact]
    public void AStillMarkeredValueIsNotCarried()
    {
        var previous = new Dictionary<string, IReadOnlyDictionary<string, CollectionsFile.Need>>
        {
            ["ledger"] = new Dictionary<string, CollectionsFile.Need> { ["server_path"] = new(null, new JsonPointer(["args", "0"])) },
        };
        var current = new Dictionary<string, McpEntry>
        {
            ["ledger"] = new(true, JsonValue.Object(
                ("command", JsonValue.String("node")),
                ("args", JsonValue.Array([JsonValue.String("${CC_NEEDS:server_path}")])))),
        };
        var newConfig = JsonValue.Object(
            ("command", JsonValue.String("node")),
            ("args", JsonValue.Array([JsonValue.String("${CC_NEEDS:server_path}")])));
        var result = CollectionApply.Apply(
            Rendered([("ledger", newConfig, new Dictionary<string, RenderedNeed> { ["server_path"] = new(null, new JsonPointer(["args", "0"])) })]),
            current, previous);
        Assert.Equal(JsonValue.String("${CC_NEEDS:server_path}"), result.Entries["ledger"].Config.ValueAt(new JsonPointer(["args", "0"])));
    }
}

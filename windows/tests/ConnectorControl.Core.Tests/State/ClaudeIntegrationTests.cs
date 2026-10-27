using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>Mirror: Tests/ConnectorControlStateTests/ClaudeIntegrationTests.swift</summary>
public class ClaudeIntegrationTests
{
    private const string Reserved = ClaudeIntegration.EntryName;
    private static readonly JsonValue Config = JsonValue.Object(("command", JsonValue.String("node")), ("args", JsonValue.Array([JsonValue.String("x.js")])));

    // MARK: Writers refuse the name

    [Fact]
    public void AddingAConnectorUnderTheReservedNameIsRefused()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Equal(ClaudeIntegration.ReservedNameError, state.Upsert(Reserved, new McpEntry(true, Config), null));
        // Trimmed first, as every name is.
        Assert.Equal(ClaudeIntegration.ReservedNameError, state.Upsert($"  {Reserved} ", new McpEntry(true, Config), null));
        Assert.False(state.Store.Mcps.ContainsKey(Reserved));
    }

    [Fact]
    public void RenamingAConnectorToTheReservedNameIsRefused()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var entry = state.Store.Mcps["scoutbook"];
        Assert.Equal(ClaudeIntegration.ReservedNameError, state.Upsert(Reserved, entry, "scoutbook"));
        // A refused rename leaves the connector where it was.
        Assert.True(state.Store.Mcps.ContainsKey("scoutbook"));
    }

    [Fact]
    public void ALegacyConnectorHoldingTheNameCanBeEditedInPlaceOrRenamedAway()
    {
        using var h = new AppStateHarness();
        MasterStoreIO.Save(new MasterStore([new(Reserved, new McpEntry(true, Config))]), h.MasterStorePath);
        using var state = h.Create();
        var edited = JsonValue.Object(("command", JsonValue.String("node")), ("args", JsonValue.Array([JsonValue.String("y.js")])));
        Assert.Null(state.Upsert(Reserved, new McpEntry(true, edited), Reserved));
        Assert.Equal(edited, state.Store.Mcps[Reserved].Config);
        Assert.Null(state.Upsert("mine", new McpEntry(true, edited), Reserved));
        Assert.False(state.Store.Mcps.ContainsKey(Reserved));
        Assert.True(state.Store.Mcps.ContainsKey("mine"));
    }

    // MARK: Readers skip it

    [Fact]
    public void TheFirstLoadNeverTakesTheEntryIntoTheStore()
    {
        using var h = new AppStateHarness();
        var servers = h.ClaudeServers().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        servers[Reserved] = ClaudeIntegration.Entry("/x/connector-control");
        ClaudeConfigIO.Write(servers, h.ClaudeConfigPath);
        using var state = h.Create();
        Assert.False(state.Store.Mcps.ContainsKey(Reserved));
        Assert.False(h.StoreOnDisk().Mcps.ContainsKey(Reserved));
        Assert.Equal(["aws-mcp", "scoutbook", "service-now"], AppStateHarness.Keys(state.Store.Mcps.Keys));
    }

    [Fact]
    public void RestoringABackupThatHoldsTheEntryNeverAdoptsIt()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var snapshot = h.Dir.File("snapshot.json");
        File.WriteAllText(snapshot, """
        {"mcpServers": {"connector-control": {"command": "/x/connector-control", "args": ["mcp"]},
                        "scoutbook": {"command": "npx", "args": ["-y", "mcp-remote", "https://scoutbook.example.com/mcp"]}}}
        """);
        state.RestoreClaudeConfig(snapshot);
        Assert.False(state.Store.Mcps.ContainsKey(Reserved));
        Assert.False(h.StoreOnDisk().Mcps.ContainsKey(Reserved));
    }

    [Fact]
    public void SubscribingSkipsADocumentsConnectorOfThatNameAndSaysWhy()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        h.Subscribe(state, CollectionDocumentSamples.WithReservedConnector(CollectionDocumentSamples.DataTeam));
        Assert.False(state.Store.Collections["Data team"].Mcps.ContainsKey(Reserved));
        Assert.Equal(ClaudeIntegration.ReservedNameError, state.SourceBinding("Data team")?.Excluded[Reserved]);
    }

    [Fact]
    public void ImportingSkipsADocumentsConnectorOfThatName()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.WriteDocument(CollectionDocumentSamples.WithReservedConnector(CollectionDocumentSamples.DataTeam), "team.json");
        Assert.Null(state.ImportCopies(path, state.ActiveCollection, new Dictionary<string, ImportChoice>(StringComparer.Ordinal)));
        Assert.False(state.Store.Mcps.ContainsKey(Reserved));
        Assert.True(state.Store.Mcps.ContainsKey("github"));
        var model = new ImportModel(state, path);
        Assert.Equal(ImportModel.SkippedBadge(ClaudeIntegration.ReservedNameError), model.Rows.First(r => r.Name == Reserved).Badge);
    }

    [Fact]
    public void AnUpdateThatAddsAConnectorOfThatNameSkipsIt()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Subscribe(state, CollectionDocumentSamples.DataTeam);
        AppStateHarness.WriteDocumentAt(CollectionDocumentSamples.WithReservedConnector(CollectionDocumentSamples.DataTeam), path);
        state.RefreshSource("Data team");
        // A connector that never comes across is no update.
        Assert.False(state.PendingUpdates.ContainsKey("Data team"));
        Assert.Null(state.ApplyPendingUpdate("Data team"));
        Assert.False(state.Store.Collections["Data team"].Mcps.ContainsKey(Reserved));
    }

    // MARK: The entry and its strings

    [Fact]
    public void TheEntryRunsTheBundledToolAsTheRelay()
    {
        Assert.Equal(
            JsonValue.Object(("command", JsonValue.String("/Applications/Connector Control.app/Contents/MacOS/connector-control")),
                             ("args", JsonValue.Array([JsonValue.String("mcp")]))),
            ClaudeIntegration.Entry("/Applications/Connector Control.app/Contents/MacOS/connector-control"));
        Assert.Equal("connector-control", ClaudeIntegration.EntryName);
    }
}

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
        MasterStoreIO.Save(MasterStore.Single([new(Reserved, new McpEntry(true, Config))]), h.MasterStorePath);
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

    // MARK: The entry in Claude's config

    private const string ToolPath = "/Applications/Connector Control.app/Contents/MacOS/connector-control";

    [Fact]
    public void TheEntryIsInClaudesConfigOnlyWhileTheSwitchIsOn()
    {
        using var h = new AppStateHarness { ControlToolPath = ToolPath };
        using var state = h.Create();
        Assert.False(h.ClaudeServers().ContainsKey(Reserved));
        Assert.Null(state.SetClaudeIntegrationEnabled(true));
        Assert.Equal(ClaudeIntegration.Entry(ToolPath), h.ClaudeServers()[Reserved]);
        Assert.True(h.Settings.ClaudeIntegrationEnabled);
        Assert.All(state.Store.Collections.Values, c => Assert.False(c.Mcps.ContainsKey(Reserved)));   // never stored
        Assert.False(h.StoreOnDisk().Mcps.ContainsKey(Reserved));
        Assert.Null(state.SetClaudeIntegrationEnabled(false));
        Assert.False(h.ClaudeServers().ContainsKey(Reserved));
        Assert.Equal(["aws-mcp", "scoutbook", "service-now"], AppStateHarness.Keys(h.ClaudeServers().Keys));
    }

    [Fact]
    public void TurningItOnWhileClaudeRunsAsksForARestart()
    {
        using var h = new AppStateHarness { ControlToolPath = ToolPath };
        h.ClaudeRunningSince(1);
        using var state = h.Create();
        Assert.False(state.NeedsClaudeRestart);
        state.SetClaudeIntegrationEnabled(true);
        Assert.True(state.NeedsClaudeRestart);
    }

    [Fact]
    public void AReloadWithTheEntryInPlaceIsNotAChange()
    {
        using var h = new AppStateHarness { ControlToolPath = ToolPath };
        using var state = h.Create();
        state.SetClaudeIntegrationEnabled(true);
        h.Notifier.Sent.Clear();
        state.Reload();
        // The entry read back from Claude's file matches the one the app renders.
        Assert.False(state.IsDirty);
        Assert.Empty(h.Notifier.Sent);
        Assert.False(state.Store.Mcps.ContainsKey(Reserved));
    }

    [Fact]
    public void WithNoBundledToolThereIsNoEntry()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.SetClaudeIntegrationEnabled(true));
        Assert.False(h.ClaudeServers().ContainsKey(Reserved));
        Assert.False(state.IsDirty);
    }

    [Fact]
    public void ThePathFollowsTheApp()
    {
        using var h = new AppStateHarness { ControlToolPath = "/old/place/connector-control" };
        h.Settings.ClaudeIntegrationEnabled = true;
        var first = h.Create();
        Assert.Equal(ClaudeIntegration.Entry("/old/place/connector-control"), h.ClaudeServers()[Reserved]);
        first.Dispose();
        h.ControlToolPath = ToolPath;
        using var second = h.Create();
        // The next launch writes the app's new location.
        Assert.Equal(ClaudeIntegration.Entry(ToolPath), h.ClaudeServers()[Reserved]);
    }

    [Fact]
    public void AClashKeepsTheEntryOutAndTheSwitchRefusesUntilItIsRenamed()
    {
        using var h = new AppStateHarness { ControlToolPath = ToolPath };
        MasterStoreIO.Save(MasterStore.Single([new(Reserved, new McpEntry(true, Config))]), h.MasterStorePath);
        using var state = h.Create();
        Assert.True(state.ClaudeIntegrationClash);
        Assert.False(state.ShowsClaudeIntegrationOffer);
        Assert.Equal(ClaudeIntegration.ClashNote, state.SetClaudeIntegrationEnabled(true));
        Assert.False(h.Settings.ClaudeIntegrationEnabled);
        // The user's own connector is what Claude runs.
        Assert.Equal(Config, h.ClaudeServers()[Reserved]);
        Assert.Null(state.SetClaudeIntegrationEnabled(false));   // turning it off always goes through
        Assert.Null(state.Upsert("mine", new McpEntry(true, Config), Reserved));
        state.ApplyInteractively();
        Assert.False(state.ClaudeIntegrationClash);
        Assert.Null(state.SetClaudeIntegrationEnabled(true));
        Assert.Equal(ClaudeIntegration.Entry(ToolPath), h.ClaudeServers()[Reserved]);
        Assert.Equal(Config, h.ClaudeServers()["mine"]);
    }

    // MARK: The one-time offer

    [Fact]
    public void TheOfferShowsUntilAnsweredAndNotNowDismissesItForGood()
    {
        using var h = new AppStateHarness { ControlToolPath = ToolPath };
        using var state = h.Create();
        Assert.True(state.ShowsClaudeIntegrationOffer);
        state.AnswerClaudeIntegrationOffer(turnOn: false);
        Assert.False(state.ShowsClaudeIntegrationOffer);
        Assert.True(h.Settings.ClaudeIntegrationOfferAnswered);
        Assert.False(h.Settings.ClaudeIntegrationEnabled);
    }

    [Fact]
    public void TurnOnFromTheOfferTurnsItOn()
    {
        using var h = new AppStateHarness { ControlToolPath = ToolPath };
        using var state = h.Create();
        state.AnswerClaudeIntegrationOffer(turnOn: true);
        Assert.False(state.ShowsClaudeIntegrationOffer);
        Assert.True(h.Settings.ClaudeIntegrationEnabled);
        Assert.Equal(ClaudeIntegration.Entry(ToolPath), h.ClaudeServers()[Reserved]);
    }

    [Fact]
    public void DecidingTheSettingAnywhereAnswersTheOffer()
    {
        using var h = new AppStateHarness { ControlToolPath = ToolPath };
        using var state = h.Create();
        state.SetClaudeIntegrationEnabled(false);
        // Off, decided in Settings or by the command line.
        Assert.False(state.ShowsClaudeIntegrationOffer);
        Assert.True(h.Settings.ClaudeIntegrationOfferAnswered);
    }

    [Fact]
    public void ThereIsNoOfferWithoutABundledTool()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.False(state.ShowsClaudeIntegrationOffer);
    }

    // MARK: Where the app runs from, and whether it can be reached

    /// <summary>A Mac app run from where it will not stay - translocated, or from its disk image - refuses to
    /// turn the integration on, and leaves an entry Claude already has exactly as it is rather than write a
    /// path that will be gone. Turning it off still works.</summary>
    [Fact]
    public void AnAppRunFromWhereItWillNotStayRefusesTheIntegrationAndLeavesTheEntry()
    {
        using var h = new AppStateHarness();
        var installed = ClaudeIntegration.Entry(ToolPath);
        h.WriteClaudeServers([.. h.ClaudeServers().Select(p => (p.Key, p.Value)), (Reserved, installed)]);
        h.Settings.ClaudeIntegrationEnabled = true;
        h.ControlToolPath = "/private/var/folders/xy/T/AppTranslocation/0A1B/d/Connector Control.app/Contents/MacOS/connector-control";
        h.ControlToolIsTransient = true;
        using var state = h.Create();
        h.Drain();
        // The entry Claude has is left as it is.
        Assert.Equal(installed, h.ClaudeServers()[Reserved]);
        Assert.False(state.IsDirty);
        Assert.Null(state.SetClaudeIntegrationEnabled(false));   // turning it off still works
        Assert.False(h.ClaudeServers().ContainsKey(Reserved));
        Assert.Equal(ClaudeIntegration.MoveToApplicationsError, state.SetClaudeIntegrationEnabled(true));
        Assert.False(h.Settings.ClaudeIntegrationEnabled);
        Assert.False(h.ClaudeServers().ContainsKey(Reserved));
    }

    /// <summary>With the switch on but no entry in Claude's config, an app run from where it will not stay
    /// adds none - not at launch, and not when an apply writes Claude's config for another reason.</summary>
    [Fact]
    public void AnAppRunFromWhereItWillNotStayAddsNoEntryWhereClaudeHasNone()
    {
        using var h = new AppStateHarness();
        h.Settings.ClaudeIntegrationEnabled = true;
        h.ControlToolPath = "/private/var/folders/xy/T/AppTranslocation/0A1B/d/Connector Control.app/Contents/MacOS/connector-control";
        h.ControlToolIsTransient = true;
        using var state = h.Create();
        h.Drain();
        Assert.False(h.ClaudeServers().ContainsKey(Reserved));
        Assert.False(state.IsDirty);
        state.SetEnabled("scoutbook", false);
        // The apply wrote Claude's config.
        Assert.False(h.ClaudeServers().ContainsKey("scoutbook"));
        Assert.False(h.ClaudeServers().ContainsKey(Reserved));
        Assert.False(state.IsDirty);
    }

    [Fact]
    public void TheOfferIsNotMadeWhereTurningItOnWouldBeRefused()
    {
        using var h = new AppStateHarness { ControlToolPath = ToolPath, ControlToolIsTransient = true };
        using var state = h.Create();
        Assert.False(state.ShowsClaudeIntegrationOffer);
    }

    [Fact]
    public void AnEndpointThatCannotBeServedIsReportedInOneSentence()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.ControlEndpointProblem);
        state.ReportControlEndpointUnavailable();
        Assert.Equal(ClaudeIntegration.EndpointUnavailableText, state.ControlEndpointProblem);
    }
}

using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>Mirror: Tests/ConnectorControlStateTests/ControlToolsGatedTests.swift</summary>
public class ControlToolsGatedTests
{
    private static readonly JsonValue Notion = JsonValue.Object(
        ("command", JsonValue.String("npx")),
        ("args", JsonValue.Array([JsonValue.String("-y"), JsonValue.String("@notionhq/notion-mcp-server")])),
        ("env", JsonValue.Object(("NOTION_TOKEN", JsonValue.String("${CC_NEEDS:NOTION_TOKEN}")))));

    private static JsonValue Args(params (string Key, JsonValue Value)[] arguments) => JsonValue.Object(arguments);

    private static JsonValue Text(string value) => JsonValue.String(value);

    private static JsonValue Names(params string[] names) => JsonValue.Array(names.Select(JsonValue.String));

    private static ControlRig Rig(bool? answer = true)
    {
        var rig = new ControlRig();
        rig.Confirmer.Scripted = answer;
        return rig;
    }

    // MARK: add_connector

    [Fact]
    public void AddingAsksWhatWouldRunThenAddsAndNamesWhatItNeeds()
    {
        using var rig = Rig();
        var result = rig.Call("add_connector", Args(("name", Text("notion")), ("config", Notion)));
        Assert.NotNull(result);
        Assert.Equal([new ConfirmationRequest(
            "Claude wants to add “notion”",
            "It would join “Default”, the collection Claude is running, switched on, and run:",
            ControlMasking.CodeLines(Notion, ControlMasking.Exposure.Dialog),
            ControlTools.TakesEffectFooter)], rig.Confirmer.Requests);
        Assert.Equal(ControlTools.AddedText("notion", "Default", ControlTools.OnWord) + "\n"
            + ControlTools.NeedsValuesText("“NOTION_TOKEN”"), result.Text);
        Assert.Equal(JsonValue.Array([JsonValue.String("NOTION_TOKEN")]), result.Structured.ValueAt(new JsonPointer(["needs"])));
        Assert.Equal(Notion, rig.H.ClaudeServers().GetValueOrDefault("notion"));
        Assert.Equal(new McpEntry(true, Notion, EditView.Json), rig.State.Store.Mcps.GetValueOrDefault("notion"));
    }

    [Fact]
    public void AddingSwitchedOffElsewhereSaysSo()
    {
        using var rig = Rig();
        Assert.Null(rig.State.AddEmptyCollection("Work"));
        rig.Call("add_connector", Args(("name", Text("notion")), ("config", Notion),
            ("collection", Text("Work")), ("enabled", JsonValue.Bool(false))));
        Assert.Equal(ControlTools.AddMessage("Work", ControlTools.OffWord), rig.Confirmer.Requests[^1].Message);
        // Nothing Claude runs changes.
        Assert.Null(rig.Confirmer.Requests[^1].Footer);
        Assert.False(rig.State.Store.Collections["Work"].Mcps["notion"].Enabled);
        Assert.False(rig.H.ClaudeServers().ContainsKey("notion"));
    }

    [Fact]
    public void AddingIsRefusedForEveryReasonTheEditorRefusesBeforeAnyoneIsAsked()
    {
        using var rig = Rig();
        rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam);
        (JsonValue Arguments, string Refusal)[] cases =
        [
            (Args(("name", Text("  ")), ("config", Notion)), AppState.NameEmptyError),
            (Args(("name", Text("connector-control")), ("config", Notion)), ClaudeIntegration.ReservedNameError),
            (Args(("name", Text("aws-mcp")), ("config", Notion)), AppState.DuplicateNameError("aws-mcp")),
            (Args(("name", Text("x")), ("config", JsonValue.Object(("args", JsonValue.Array([]))))), ControlTools.ConfigShapeError),
            (Args(("name", Text("x")), ("config", JsonValue.Object(("command", Text("npx")),
                ("args", JsonValue.Array([Text("-y"), Text("mcp-remote"), Text("nope")]))))),
             EditorModel.InvalidUrlError),
            (Args(("name", Text("x")), ("config", Notion), ("collection", Text("Data team"))), CollectionsModel.AddConnectorDisabledTooltip),
            (Args(("name", Text("x")), ("config", Notion), ("collection", Text("Nope"))), MasterStore.NoCollectionError("Nope")),
        ];
        foreach (var (arguments, refusal) in cases)
        {
            Assert.Equal(ControlResult.Refusal(refusal), rig.Call("add_connector", arguments));
        }
        Assert.Empty(rig.Confirmer.Requests);
    }

    /// <summary>Windows only: the Mac never writes the cmd /c launcher, so its mirror has no such check.</summary>
    [Fact]
    public void ACmdLauncherWithUnsafeCharactersIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var rig = Rig();
        var config = RemotePattern.Make("https://x.example/mcp?a=1&b=2", RemoteLaunchStyle.CmdNpx);
        Assert.Equal(ControlResult.Refusal(RemotePattern.CmdUnsafeReason(RemoteField.Url)),
            rig.Call("add_connector", Args(("name", Text("x")), ("config", config))));
        Assert.Equal(ControlResult.Refusal(RemotePattern.CmdUnsafeReason(RemoteField.Url)),
            rig.Call("update_connector", Args(("name", Text("scoutbook")), ("config", config))));
        Assert.Empty(rig.Confirmer.Requests);
    }

    // MARK: update_connector

    [Fact]
    public void ChangingShowsOnlyWhatDiffersAndKeepsTheSwitch()
    {
        using var rig = Rig();
        var before = rig.State.Store.Mcps["scoutbook"];
        var after = AppStateHarness.Remote("https://scoutbook.example.com/v2/mcp");
        var result = rig.Call("update_connector", Args(("name", Text("scoutbook")), ("config", after)));
        Assert.NotNull(result);
        Assert.Equal(new ConfirmationRequest(
            "Claude wants to change “scoutbook”",
            "In “Default”, the collection Claude is running:",
            ControlMasking.ChangeLines(before.Config, after, ControlMasking.Exposure.Dialog),
            ControlTools.TakesEffectFooter), rig.Confirmer.Requests[^1]);
        Assert.Equal(ControlTools.ChangedText("scoutbook", "Default"), result.Text);
        Assert.Equal(after, rig.State.Store.Mcps["scoutbook"].Config);
        Assert.Equal(before.Enabled, rig.State.Store.Mcps["scoutbook"].Enabled);
        Assert.Equal(after, rig.H.ClaudeServers().GetValueOrDefault("scoutbook"));
    }

    [Fact]
    public void RenamingThroughUpdate()
    {
        using var rig = Rig();
        var result = rig.Call("update_connector", Args(("name", Text("scoutbook")), ("newName", Text("scouts"))));
        Assert.NotNull(result);
        Assert.Equal([ControlMasking.NameLabel, ControlMasking.RemovedPrefix + "scoutbook", ControlMasking.AddedPrefix + "scouts"],
            rig.Confirmer.Requests[^1].Code);
        Assert.Equal(ControlTools.RenamedText("scoutbook", "scouts"), result.Text);
        Assert.False(rig.State.Store.Mcps.ContainsKey("scoutbook"));
        Assert.True(rig.H.ClaudeServers().ContainsKey("scouts"));
    }

    [Fact]
    public void ChangingIsRefusedBeforeAnyoneIsAsked()
    {
        using var rig = Rig();
        rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam);
        var same = rig.State.Store.Mcps["scoutbook"].Config;
        (JsonValue Arguments, string Refusal)[] cases =
        [
            (Args(("name", Text("nope")), ("config", Notion)), ControlTools.NoConnectorError("nope", "Default")),
            (Args(("name", Text("scoutbook"))), ControlTools.NothingToChangeError),
            (Args(("name", Text("scoutbook")), ("config", same)), ControlTools.UnchangedError("scoutbook")),
            (Args(("name", Text("scoutbook")), ("newName", Text("aws-mcp"))), AppState.DuplicateNameError("aws-mcp")),
            (Args(("name", Text("scoutbook")), ("newName", Text("connector-control"))), ClaudeIntegration.ReservedNameError),
            (Args(("name", Text("scoutbook")), ("newName", Text(" "))), AppState.NameEmptyError),
            (Args(("name", Text("scoutbook")), ("config", JsonValue.Object())), ControlTools.ConfigShapeError),
            (Args(("name", Text("github")), ("config", Notion), ("collection", Text("Data team"))), CollectionsModel.LockedGlyphTooltip),
        ];
        foreach (var (arguments, refusal) in cases)
        {
            Assert.Equal(ControlResult.Refusal(refusal), rig.Call("update_connector", arguments));
        }
        Assert.Empty(rig.Confirmer.Requests);
    }

    // MARK: delete_connectors

    [Fact]
    public void DeletingAsksThenDeletes()
    {
        using var rig = Rig();
        var result = rig.Call("delete_connectors", Args(("names", Names("aws-mcp", "scoutbook"))));
        Assert.NotNull(result);
        Assert.Equal(new ConfirmationRequest(
            "Claude wants to delete “aws-mcp” and “scoutbook”",
            "From “Default”, the collection Claude is running. A copy remains in Backups.",
            [], ControlTools.TakesEffectFooter), rig.Confirmer.Requests[^1]);
        Assert.Equal(ControlTools.DeletedText("“aws-mcp” and “scoutbook”", "Default"), result.Text);
        Assert.Equal([ClaudeIntegration.EntryName, "service-now"], rig.H.ClaudeServers().Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DeletingIsRefusedForANameThatIsNotThereOrASubscribedCollection()
    {
        using var rig = Rig();
        rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam);
        Assert.Equal(ControlResult.Refusal(ControlTools.NoConnectorError("nope", "Default")),
            rig.Call("delete_connectors", Args(("names", Names("aws-mcp", "nope")))));
        Assert.Equal(ControlResult.Refusal(ControlTools.MissingArgumentError("names")),
            rig.Call("delete_connectors", Args(("names", Names()))));
        Assert.Equal(ControlResult.Refusal(CollectionsModel.LockedGlyphTooltip),
            rig.Call("delete_connectors", Args(("names", Names("github")), ("collection", Text("Data team")))));
        // Nothing is deleted when one name is wrong.
        Assert.True(rig.State.Store.Mcps.ContainsKey("aws-mcp"));
        Assert.Empty(rig.Confirmer.Requests);
    }
}

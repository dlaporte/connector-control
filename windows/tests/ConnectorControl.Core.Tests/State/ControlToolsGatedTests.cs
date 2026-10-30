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

    /// <summary>A config the masked view and the dialog show differently: an argument after a flag named for
    /// a secret, a URL with a query value, and an environment value.</summary>
    private static readonly JsonValue Secretive = JsonValue.Object(
        ("command", JsonValue.String("npx")),
        ("args", JsonValue.Array([JsonValue.String("-y"), JsonValue.String("acme-mcp"), JsonValue.String("--api-key"),
            JsonValue.String("sk-live-abc123"), JsonValue.String("https://h.example/mcp?token=abc")])),
        ("env", JsonValue.Object(("ACME_TOKEN", JsonValue.String("literal-env-secret")))));

    /// <summary>The same config twice over, but for how one character is composed: é as one character, and
    /// as e and a combining acute accent.</summary>
    private static readonly JsonValue Composed = JsonValue.Object(("command", JsonValue.String("node")),
        ("args", JsonValue.Array([JsonValue.String("caf\u00E9.js")])));
    private static readonly JsonValue Decomposed = JsonValue.Object(("command", JsonValue.String("node")),
        ("args", JsonValue.Array([JsonValue.String("cafe\u0301.js")])));

    private static JsonValue Args(params (string Key, JsonValue Value)[] arguments) => JsonValue.Object(arguments);

    private static JsonValue Text(string value) => JsonValue.String(value);

    private static JsonValue Names(params string[] names) => JsonValue.Array(names.Select(JsonValue.String));

    private static ControlRig Rig(bool? answer = true)
    {
        var rig = new ControlRig();
        rig.Confirmer.Scripted = answer;
        return rig;
    }

    /// <summary>Nothing in <paramref name="text"/> that a line would show as an escape: nothing that breaks a
    /// line, or reorders or hides text.</summary>
    private static void AssertPlain(string text) => Assert.DoesNotContain(text, c => ControlText.Escape(c) is not null);

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
            (Args(("name", Text("x")), ("config", JsonValue.Object(("url", Text(" "))))), ControlTools.ConfigShapeError),
            (Args(("name", Text("x")), ("config", JsonValue.Object(("command", Text("npx")),
                ("args", JsonValue.Array([Text("-y"), Text("mcp-remote"), Text("nope")]))))),
             EditorModel.InvalidUrlError),
            (Args(("name", Text("x")), ("config", Notion), ("collection", Text("Data team"))), ControlTools.SubscribedReadOnlyError("Data team")),
            (Args(("name", Text("x")), ("config", Notion), ("collection", Text("Nope"))), MasterStore.NoCollectionError("Nope")),
        ];
        foreach (var (arguments, refusal) in cases)
        {
            Assert.Equal(ControlResult.Refusal(refusal), rig.Call("add_connector", arguments));
        }
        Assert.Empty(rig.Confirmer.Requests);
    }

    /// <summary>This mirror only: the Mac never writes the cmd /c launcher, so its mirror has no such check.
    /// The rule is the same on every machine, so the test runs everywhere.</summary>
    [Fact]
    public void ACmdLauncherWithUnsafeCharactersIsRefused()
    {
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

    /// <summary>A config that differs only in how a character is composed is a different config, as it is
    /// on the Mac, which compares scalar by scalar: the change is asked about, not refused as no change.</summary>
    [Fact]
    public void AChangeOnlyInHowACharacterIsComposedIsStillAChange()
    {
        using var rig = Rig();
        Assert.Null(rig.State.Upsert("scoutbook", new McpEntry(Composed), "scoutbook"));
        var result = rig.Call("update_connector", Args(("name", Text("scoutbook")), ("config", Decomposed)));
        Assert.Equal(ControlTools.ChangedText("scoutbook", "Default"), result?.Text);
        Assert.Single(rig.Confirmer.Requests);
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
            (Args(("name", Text("github")), ("config", Notion), ("collection", Text("Data team"))), ControlTools.SubscribedReadOnlyError("Data team")),
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
        Assert.Equal(ControlResult.Refusal(ControlTools.SubscribedReadOnlyError("Data team")),
            rig.Call("delete_connectors", Args(("names", Names("github")), ("collection", Text("Data team")))));
        // Nothing is deleted when one name is wrong.
        Assert.True(rig.State.Store.Mcps.ContainsKey("aws-mcp"));
        Assert.Empty(rig.Confirmer.Requests);
    }

    // MARK: what the question shows

    /// <summary>The dialog shows what would run, argument and URL as written, with a secret's environment
    /// value masked, where the masked view would hide all three; the answer to the caller shows none
    /// of them.</summary>
    [Fact]
    public void TheQuestionShowsWhatWouldRunAndTheAnswerNoSecret()
    {
        using var rig = Rig();
        var added = rig.Call("add_connector", Args(("name", Text("acme")), ("config", Secretive)));
        var changed = rig.Call("update_connector", Args(("name", Text("scoutbook")), ("config", Secretive)));
        Assert.Equal(2, rig.Confirmer.Requests.Count);
        // The masked view hides them.
        Assert.NotEqual(ControlMasking.CodeLines(Secretive), rig.Confirmer.Requests[0].Code);
        foreach (var request in rig.Confirmer.Requests)
        {
            var code = string.Join("\n", request.Code);
            Assert.Contains("--api-key sk-live-abc123 https://h.example/mcp?token=abc", code);
            Assert.Contains("ACME_TOKEN", code);
            Assert.DoesNotContain("literal-env-secret", code);
        }
        foreach (var result in new[] { added, changed })
        {
            Assert.NotNull(result);
            Assert.False(result.IsError);
            foreach (var secret in new[] { "sk-live-abc123", "token=abc", "literal-env-secret" })
            {
                Assert.DoesNotContain(secret, result.Text);
                Assert.DoesNotContain(secret, result.Structured.CompactLine());
            }
        }
    }

    /// <summary>The question shows an environment value as written, since a variable can change what runs,
    /// unless its name reads as a secret's: an add lists it, and a change to it alone shows what it
    /// was and what it becomes. The answers to the caller carry none of it.</summary>
    [Fact]
    public void TheQuestionShowsAnEnvironmentValueAndTheAnswerNone()
    {
        using var rig = Rig();
        static JsonValue Hooked(string hook) => JsonValue.Object(("command", Text("node")), ("args", JsonValue.Array([Text("server.js")])),
            ("env", JsonValue.Object(("NODE_OPTIONS", Text("--require " + hook)), ("ACME_TOKEN", Text("literal-env-secret")))));
        var added = rig.Call("add_connector", Args(("name", Text("acme")), ("config", Hooked("./a.js"))));
        var changed = rig.Call("update_connector", Args(("name", Text("acme")), ("config", Hooked("./b.js"))));
        Assert.Equal(2, rig.Confirmer.Requests.Count);
        Assert.Equal(["command   node", "args      server.js", "env       ACME_TOKEN = ••• (set)", "          NODE_OPTIONS = '--require ./a.js'"],
            rig.Confirmer.Requests[0].Code);
        Assert.Equal(["env", "  − NODE_OPTIONS = '--require ./a.js'", "  + NODE_OPTIONS = '--require ./b.js'"],
            rig.Confirmer.Requests[1].Code);
        foreach (var result in new[] { added, changed })
        {
            Assert.NotNull(result);
            Assert.False(result.IsError, result.Text);
            foreach (var secret in new[] { "a.js", "b.js", "literal-env-secret" })
            {
                Assert.DoesNotContain(secret, result.Text);
                Assert.DoesNotContain(secret, result.Structured.CompactLine());
            }
        }
    }

    /// <summary>The question's names are chosen by others: a connector's by Claude, a collection's by Claude
    /// too (it creates and renames collections without asking), and the asker's by whichever client
    /// connects. Each shows as an escape what would break a line or reorder or hide text.</summary>
    [Fact]
    public void NamesCannotForgeWhatTheQuestionSays()
    {
        using var rig = Rig();
        const string collection = "Work\n\u202Ez", shownCollection = """Work\n\u{202E}z""";
        const string name = "a\nb\u202Ec", shownName = """a\nb\u{202E}c""";
        var caller = new ControlCaller.Relay("Cursor\n\u202Ed");
        var shownAsker = ControlCaller.CallingItselfText("""Cursor\n\u{202E}d""");
        Assert.Null(rig.State.AddEmptyCollection(collection));
        ControlResult?[] results =
        [
            rig.Call("add_connector", Args(("collection", Text(collection)), ("name", Text(name)), ("config", Notion)), caller),
            rig.Call("update_connector", Args(("collection", Text(collection)), ("name", Text(name)), ("newName", Text("b"))), caller),
            rig.Call("delete_connectors", Args(("collection", Text(collection)), ("names", Names("b"))), caller),
            rig.Call("delete_connectors", Args(("collection", Text(collection)), ("names", Names(name))), caller),
        ];
        // The answers name them so too.
        Assert.Equal([
            ControlTools.AddedText(shownName, shownCollection, ControlTools.OnWord) + "\n" + ControlTools.NeedsValuesText("“NOTION_TOKEN”"),
            ControlTools.RenamedText(shownName, "b"),
            ControlTools.DeletedText("“b”", shownCollection),
            ControlTools.NoConnectorError(shownName, shownCollection),
        ], results.Select(result => result?.Text));
        var requests = rig.Confirmer.Requests;
        Assert.Equal([ControlTools.AddTitle(shownAsker, shownName), ControlTools.ChangeTitle(shownAsker, shownName),
                      ControlTools.DeleteTitle(shownAsker, "“b”")], requests.Select(request => request.Title));
        Assert.Equal([ControlTools.AddMessage(shownCollection, ControlTools.OnWord), ControlTools.ChangeMessage(shownCollection),
                      ControlTools.DeleteMessage(shownCollection)], requests.Select(request => request.Message));
        Assert.Equal([ControlMasking.NameLabel, ControlMasking.RemovedPrefix + shownName, ControlMasking.AddedPrefix + "b"],
            requests[1].Code);
        foreach (var request in requests)
        {
            foreach (var text in new[] { request.Title, request.Message }.Concat(request.Code))
            {
                AssertPlain(text);
            }
        }
        foreach (var line in results.SelectMany(result => (result?.Text ?? "").Split('\n')))
        {
            AssertPlain(line);
        }
        // Added, renamed and deleted as asked.
        Assert.Empty(rig.State.Store.Collections[collection].Mcps);
    }

    /// <summary>A name cannot close the quotes around it, so one name never reads as two.</summary>
    [Fact]
    public void ANameCannotReadAsTwoNames()
    {
        using var rig = Rig();
        const string name = "x” and “github";
        Assert.Null(rig.State.Upsert(name, new McpEntry(Notion), null));
        rig.Call("delete_connectors", Args(("names", Names(name))));
        Assert.Equal(ControlTools.DeleteTitle("Claude", """“x\u{201D} and \u{201C}github”"""), rig.Confirmer.Requests[^1].Title);
    }

    /// <summary>A new name is one line of what the question shows, however many lines it holds.</summary>
    [Fact]
    public void ANewNameCannotAddLinesToWhatTheQuestionShows()
    {
        using var rig = Rig();
        rig.Call("update_connector", Args(("name", Text("scoutbook")), ("newName", Text("a\n  + command   evil"))));
        Assert.Equal([ControlMasking.NameLabel, ControlMasking.RemovedPrefix + "scoutbook",
                      ControlMasking.AddedPrefix + """a\n  + command   evil"""], rig.Confirmer.Requests[^1].Code);
    }
}

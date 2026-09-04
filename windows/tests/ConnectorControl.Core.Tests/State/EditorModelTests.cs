using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

public class EditorModelTests
{
    private const string Url = "https://scoutbook.example.com/mcp";

    private static JsonValue Local(string command, string[] args, (string Key, string Value)[]? env = null, (string Key, JsonValue Value)[]? extra = null)
    {
        var props = new List<(string, JsonValue)> { ("command", JsonValue.String(command)), ("args", JsonValue.Array(args.Select(JsonValue.String))) };
        if (env is { Length: > 0 })
        {
            props.Add(("env", JsonValue.Object(env.Select(e => (e.Key, JsonValue.String(e.Value))).ToArray())));
        }
        if (extra is not null)
        {
            props.AddRange(extra);
        }
        return JsonValue.Object(props.ToArray());
    }

    private static EditorModel Editor(AppStateHarness h, AppState state, EditTarget target) =>
        new(state, target, h.Dialogs, RemoteLaunchStyle.CmdNpx);

    [Fact]
    public void NewRemoteTargetOpensInTheRemoteFormWithAnEmptyUrl()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        Assert.Equal("Add Connector", editor.WindowTitle);
        Assert.Equal(EditView.Form, editor.View);
        Assert.True(editor.ShowTypePicker);
        Assert.True(editor.IsRemote);
        Assert.Equal("", editor.RemoteUrl);
        Assert.False(editor.ShowUrlHint);   // hint only once something invalid is typed
        Assert.False(editor.CanSave);
        Assert.False(editor.CanRemove);
        Assert.Equal(RemoteAuthKind.Automatic, editor.AuthKind);
    }

    [Theory]
    [InlineData("npx")]
    [InlineData("cmd")]
    public void ExistingBareRemoteOpensInTheRemoteFormInEitherLaunchStyle(string command)
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var config = command == "npx"
            ? Local("npx", ["-y", "mcp-remote", Url])
            : Local("cmd", ["/c", "npx", "-y", "mcp-remote", Url]);
        var editor = Editor(h, state, EditTarget.Existing("scoutbook", new McpEntry(config)));
        Assert.Equal("Edit “scoutbook”", editor.WindowTitle);
        Assert.False(editor.ShowTypePicker);
        Assert.True(editor.IsRemote);
        Assert.Equal(Url, editor.RemoteUrl);
        Assert.True(editor.CanSave);
        Assert.True(editor.CanRemove);
    }

    [Fact]
    public void ExistingRemoteWithAuthFlagsOpensInTheLocalForm()
    {
        // Catalog §3.3: detect() requires exactly two stripped args, so auth flags push the connector into the Local form
        // even though decode() populated the auth fields. Reproduced as-is.
        using var h = new AppStateHarness();
        using var state = h.Create();
        var config = RemotePattern.Encode(new RemoteConfig(Url, new RemoteAuth.Bearer("tok"), RemoteLaunchStyle.Npx));
        var editor = Editor(h, state, EditTarget.Existing("scoutbook", new McpEntry(config)));
        Assert.False(editor.IsRemote);
        Assert.Equal("npx", editor.Command);
        Assert.Equal(["-y", "mcp-remote", Url, "--header", "Authorization:${AUTH_HEADER}"], editor.Args.Select(a => a.Value).ToArray());
        var row = Assert.Single(editor.EnvRows);
        Assert.Equal("AUTH_HEADER", row.Name);
        Assert.Equal("Bearer tok", row.Value);
        Assert.False(row.Revealed);
        Assert.Equal(RemoteAuthKind.Bearer, editor.AuthKind);
        Assert.Equal("tok", editor.BearerToken);
    }

    [Fact]
    public void ExistingLocalOpensWithArgsEnvAndAdditionalFields()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var config = Local("node", ["server.js", "--port", "3000"], [("TOKEN", "s3cret"), ("A", "1")], [("disabled", JsonValue.Bool(false)), ("type", JsonValue.String("stdio"))]);
        var editor = Editor(h, state, EditTarget.Existing("local", new McpEntry(false, config, EditView.Json)));
        Assert.Equal(EditView.Json, editor.View);   // reopens in the view last used to save
        editor.RequestView(EditView.Form);
        Assert.False(editor.IsRemote);
        Assert.Equal("node", editor.Command);
        Assert.Equal(["server.js", "--port", "3000"], editor.Args.Select(a => a.Value).ToArray());
        Assert.Equal(["A", "TOKEN"], editor.EnvRows.Select(r => r.Name).ToArray());   // sorted by key
        Assert.True(editor.HasAdditional);
        Assert.Equal("2 field(s) not editable here: disabled, type — switch to JSON to edit", editor.AdditionalTitle);
        Assert.Equal("{\n  \"disabled\" : false,\n  \"type\" : \"stdio\"\n}", editor.AdditionalPreview);
    }

    [Fact]
    public void SwitchingANewTargetToLocalResetsTheBridgeInvocation()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        editor.RemoteUrl = Url;
        editor.IsRemote = false;
        Assert.Equal("npx", editor.Command);
        Assert.Equal(["-y", ""], editor.Args.Select(a => a.Value).ToArray());
        editor.IsRemote = true;
        Assert.Equal(Url, editor.RemoteUrl);   // switching back changes nothing
    }

    [Fact]
    public void FormToJsonSyncsTheTextAndJsonToFormAdoptsIt()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        editor.RemoteUrl = Url;
        editor.RequestView(EditView.Json);
        Assert.Equal(EditView.Json, editor.View);
        Assert.Equal("{\n  \"args\" : [\n    \"/c\",\n    \"npx\",\n    \"-y\",\n    \"mcp-remote\",\n    \"" + Url + "\"\n  ],\n  \"command\" : \"cmd\"\n}", editor.JsonText);
        Assert.Null(editor.JsonError);
        Assert.Equal(EditorModel.JsonTip, editor.JsonStatusText);

        editor.JsonText = "{\"command\": \"node\", \"args\": [\"x.js\"], \"env\": {\"K\": \"v\"}}";
        editor.RequestView(EditView.Form);
        Assert.Equal(EditView.Form, editor.View);
        Assert.False(editor.IsRemote);
        Assert.Equal("node", editor.Command);
        Assert.Equal(["x.js"], editor.Args.Select(a => a.Value).ToArray());
        Assert.False(editor.EnvRows[0].Revealed);   // re-adopted values are masked again
    }

    [Fact]
    public void FormToJsonIsBlockedByEnvValidation()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.New(Local("node", ["x.js"])));
        editor.AddEnvRow();
        editor.EnvRows[0].Value = "orphan";
        editor.RequestView(EditView.Json);
        Assert.Equal(EditView.Form, editor.View);
        Assert.Equal("An environment variable value is missing its name.", editor.ValidationError);
        Assert.False(editor.IsJsonView);
    }

    [Fact]
    public void JsonToFormWithLossPromptsAndStaysUnlessForced()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.New(Local("node", ["x.js"])));
        editor.RequestView(EditView.Json);
        editor.JsonText = "{\"command\": 1, \"args\": [\"a\", 2], \"env\": {\"K\": true}}";
        h.Dialogs.NextConfirm = false;
        editor.RequestView(EditView.Form);
        Assert.Equal(EditView.Json, editor.View);
        var call = Assert.Single(h.Dialogs.Confirms);
        Assert.Equal("Switching to Form view can’t fully represent this configuration. These elements would be lost or altered:\nargs[1] (number)\ncommand (number)\nenv.K (boolean)", call.Message);
        Assert.Equal("Switch Anyway", call.Primary);
        Assert.Equal("Stay in JSON", call.Cancel);
        Assert.True(call.Destructive);

        h.Dialogs.NextConfirm = true;
        editor.RequestView(EditView.Form);
        Assert.Equal(EditView.Form, editor.View);
        Assert.Equal("", editor.Command);
        Assert.Equal(["a"], editor.Args.Select(a => a.Value).ToArray());
        Assert.Empty(editor.EnvRows);
    }

    [Fact]
    public void JsonValidationErrorDisablesSave()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.New(Local("node", ["x.js"])));
        editor.RequestView(EditView.Json);
        editor.JsonText = "{\"command\": ";
        Assert.Equal("Not valid JSON — check for a stray brace, missing comma, or unquoted value.", editor.JsonError);
        Assert.Equal(editor.JsonError, editor.JsonStatusText);
        Assert.True(editor.HasJsonError);
        Assert.False(editor.CanSave);
        editor.JsonText = "{\"command\": \"node\"}";
        Assert.Null(editor.JsonError);
        Assert.True(editor.CanSave);
    }

    [Fact]
    public void JsonPasteFillsTheNameWhenBlankAndCanonicalizesTheText()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        editor.RequestView(EditView.Json);
        editor.JsonText = "{\"mcpServers\": {\"pasted\": {\"command\": \"node\", \"args\": [\"x.js\"]}}}";
        Assert.True(editor.Save());
        Assert.Equal("pasted", editor.Name);
        Assert.Equal("{\n  \"args\" : [\n    \"x.js\"\n  ],\n  \"command\" : \"node\"\n}", editor.JsonText);
        Assert.Equal(EditView.Json, state.Store.Mcps["pasted"].LastEditView);
    }

    [Theory]
    [InlineData(RemoteAuthKind.Automatic, "", "", "", "", "Server URL must be a valid http(s) URL.")]
    [InlineData(RemoteAuthKind.Bearer, Url, "", "", "", "Enter a bearer token.")]
    [InlineData(RemoteAuthKind.Header, Url, "", "", "", "Enter a header name.")]
    [InlineData(RemoteAuthKind.Header, Url, "", "X-API-Key", "", "Enter a header value.")]
    [InlineData(RemoteAuthKind.OAuthClient, Url, "", "", "", "Enter a client ID.")]
    public void SaveValidatesTheRemoteForm(RemoteAuthKind kind, string url, string token, string headerName, string headerValue, string expected)
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        editor.Name = "r";
        editor.RemoteUrl = url;
        editor.AuthKindIndex = EditorModel.AuthKinds.ToList().IndexOf(kind);
        editor.BearerToken = token;
        editor.HeaderName = headerName;
        editor.HeaderValue = headerValue;
        Assert.False(editor.Save());
        Assert.Equal(expected, editor.ValidationError);
        Assert.False(state.Store.Mcps.ContainsKey("r"));
    }

    [Fact]
    public void SaveValidatesTheLocalForm()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.New(Local("", [])));
        editor.Name = "l";
        Assert.False(editor.Save());
        Assert.Equal("Command must not be empty.", editor.ValidationError);

        editor.Command = "node";
        editor.AddEnvRow();
        editor.EnvRows[0].Name = "K";
        editor.AddEnvRow();
        editor.EnvRows[1].Name = "K";
        Assert.False(editor.Save());
        Assert.Equal("Duplicate environment variable name: K", editor.ValidationError);
    }

    [Fact]
    public void SaveRejectsACanonicalBridgeShapeWithAnInvalidUrl()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.New(Local("node", [])));
        editor.RequestView(EditView.Json);
        editor.JsonText = "{\"command\": \"npx\", \"args\": [\"-y\", \"mcp-remote\", \"nope\"]}";
        editor.Name = "bad";
        Assert.False(editor.Save());
        Assert.Equal("Server URL must be a valid http(s) URL.", editor.ValidationError);
    }

    [Fact]
    public void SaveNewRemoteWritesTheCmdNpxShapeAndAppliesImmediately()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        var closed = 0;
        editor.CloseRequested += () => closed++;
        editor.Name = "new-remote";
        editor.RemoteUrl = "https://new.example/mcp";
        Assert.True(editor.Save());
        Assert.Equal(1, closed);
        var entry = state.Store.Mcps["new-remote"];
        Assert.True(entry.Enabled);
        Assert.Equal(EditView.Form, entry.LastEditView);
        Assert.Equal(Local("cmd", ["/c", "npx", "-y", "mcp-remote", "https://new.example/mcp"]), entry.Config);
        Assert.True(h.ClaudeServers().ContainsKey("new-remote"));
        Assert.Equal(h.Now, h.Settings.LastApplyDate);
    }

    [Theory]
    [InlineData(RemoteAuthKind.Bearer)]
    [InlineData(RemoteAuthKind.Header)]
    [InlineData(RemoteAuthKind.OAuthClient)]
    public void SaveEncodesEachAuthKind(RemoteAuthKind kind)
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        editor.Name = "auth";
        editor.RemoteUrl = Url;
        editor.AuthKindIndex = EditorModel.AuthKinds.ToList().IndexOf(kind);
        editor.BearerToken = "tok";
        editor.HeaderName = "X-API-Key";
        editor.HeaderValue = "v";
        editor.OAuthClientId = "id";
        editor.OAuthClientSecret = "sec";
        editor.OAuthScopes = "a b";
        Assert.True(editor.Save());
        RemoteAuth expected = kind switch
        {
            RemoteAuthKind.Bearer => new RemoteAuth.Bearer("tok"),
            RemoteAuthKind.Header => new RemoteAuth.Header("X-API-Key", "v"),
            _ => new RemoteAuth.OAuthClient("id", "sec", "a b"),
        };
        Assert.Equal(RemotePattern.Encode(new RemoteConfig(Url, expected, RemoteLaunchStyle.CmdNpx)), state.Store.Mcps["auth"].Config);
    }

    [Fact]
    public void SaveExistingPreservesTheEnabledStateAndRecordsTheView()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        state.SetEnabled("scoutbook", false);
        var editor = Editor(h, state, EditTarget.Existing("scoutbook", state.Store.Mcps["scoutbook"]));
        editor.RemoteUrl = "https://moved.example/mcp";
        editor.RequestView(EditView.Json);
        Assert.True(editor.Save());
        var entry = state.Store.Mcps["scoutbook"];
        Assert.False(entry.Enabled);
        Assert.Equal(EditView.Json, entry.LastEditView);
        Assert.Equal(Local("npx", ["-y", "mcp-remote", "https://moved.example/mcp"]), entry.Config);   // decoded as bare npx, re-encoded as bare npx
        Assert.False(h.ClaudeServers().ContainsKey("scoutbook"));   // disabled: not applied to Claude
    }

    [Fact]
    public void SaveRenameRemovesTheOldKeyAndNameErrorsSurface()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.Existing("scoutbook", state.Store.Mcps["scoutbook"]));
        editor.Name = "aws-mcp";
        Assert.False(editor.Save());
        Assert.Equal("A connector named “aws-mcp” already exists.", editor.ValidationError);
        editor.Name = " ";
        Assert.False(editor.Save());
        Assert.Equal("Name must not be empty.", editor.ValidationError);
        editor.Name = "scoutbook2";
        Assert.True(editor.Save());
        Assert.False(state.Store.Mcps.ContainsKey("scoutbook"));
        Assert.True(state.Store.Mcps.ContainsKey("scoutbook2"));
        Assert.True(h.ClaudeServers().ContainsKey("scoutbook2"));
    }

    [Fact]
    public void SaveConflictWhenTheEntryChangedOutsideTheEditor()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.Existing("scoutbook", state.Store.Mcps["scoutbook"]));
        state.Upsert("scoutbook", new McpEntry(AppStateHarness.Remote("https://elsewhere.example/mcp")), "scoutbook");
        editor.RemoteUrl = "https://mine.example/mcp";
        h.Dialogs.NextConfirm = false;
        Assert.False(editor.Save());
        var call = Assert.Single(h.Dialogs.Confirms);
        Assert.Equal(new FakeDialogs.ConfirmCall("“scoutbook” changed outside this editor.", "Saving will overwrite that change with this editor's version.", "Save Anyway", "Cancel", false), call);
        Assert.Equal(AppStateHarness.Remote("https://elsewhere.example/mcp"), state.Store.Mcps["scoutbook"].Config);

        h.Dialogs.NextConfirm = true;
        Assert.True(editor.Save());
        Assert.Equal(Local("npx", ["-y", "mcp-remote", "https://mine.example/mcp"]), state.Store.Mcps["scoutbook"].Config);
    }

    [Fact]
    public void SaveConflictWhenTheEntryWasRemovedOutsideTheEditor()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.Existing("scoutbook", state.Store.Mcps["scoutbook"]));
        state.Remove("scoutbook");
        Assert.True(editor.Save());
        Assert.Equal(new FakeDialogs.ConfirmCall("“scoutbook” was removed outside this editor.", "Saving will add it back.", "Save Anyway", "Cancel", false), h.Dialogs.Confirms[0]);
        Assert.True(state.Store.Mcps["scoutbook"].Enabled);   // a re-added entry takes the editor's snapshot enabled state
    }

    [Fact]
    public void RemoveConfirmsThenRemovesAndAppliesInOneTurn()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.Existing("scoutbook", state.Store.Mcps["scoutbook"]));
        var closed = 0;
        editor.CloseRequested += () => closed++;
        h.Dialogs.NextConfirm = false;
        editor.Remove();
        Assert.Equal(new FakeDialogs.ConfirmCall("Remove “scoutbook”? A copy remains in Backups.", null, "Remove", "Cancel", true), h.Dialogs.Confirms[0]);
        Assert.True(state.Store.Mcps.ContainsKey("scoutbook"));
        Assert.Equal(0, closed);

        h.Dialogs.NextConfirm = true;
        editor.Remove();
        Assert.False(state.Store.Mcps.ContainsKey("scoutbook"));
        Assert.False(h.ClaudeServers().ContainsKey("scoutbook"));
        Assert.Equal(1, closed);
    }

    [Fact]
    public void CancelClosesWithoutPersisting()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.Existing("scoutbook", state.Store.Mcps["scoutbook"]));
        var closed = 0;
        editor.CloseRequested += () => closed++;
        editor.RemoteUrl = "https://edited.example/mcp";
        editor.Cancel();
        Assert.Equal(1, closed);
        Assert.Equal(Url, RemotePattern.Detect(state.Store.Mcps["scoutbook"].Config));
    }

    [Fact]
    public void EnvRowsAreMaskedExceptFreshlyAddedOnes()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.New(Local("node", [], [("K", "v")])));
        Assert.False(editor.EnvRows[0].Revealed);
        editor.ToggleReveal(editor.EnvRows[0]);
        Assert.True(editor.EnvRows[0].Revealed);
        EnvRow? focused = null;
        editor.FocusEnvRowRequested += row => focused = row;
        editor.AddEnvRow();
        Assert.True(editor.EnvRows[1].Revealed);
        Assert.Same(editor.EnvRows[1], focused);
        editor.RemoveEnvRow(editor.EnvRows[0]);
        Assert.Single(editor.EnvRows);
        editor.AddArg();
        editor.Args[0].Value = "--flag";
        editor.RemoveArg(editor.Args[0]);
        Assert.Empty(editor.Args);
    }

    [Fact]
    public void AuthKindTitlesMatchTheMacApp()
    {
        Assert.Equal(["Automatic (OAuth / none)", "Bearer token", "Custom header", "OAuth client ID/secret"], EditorModel.AuthKindTitles.ToArray());
        Assert.Equal("Runs via npx mcp-remote — managed for you.", EditorModel.RemoteFooter);
        Assert.Equal("Enter a valid http(s) URL, e.g. https://example.com/mcp", EditorModel.UrlHint);
        Assert.Equal("Uses the server's OAuth (a browser window opens on first use), or no auth if the server is open.", EditorModel.AutomaticCaption);
        Assert.Equal("Sent as Authorization: Bearer …", EditorModel.BearerCaption);
        Assert.Equal("＋ Add argument", EditorModel.AddArgumentTitle);
        Assert.Equal("＋ Add variable", EditorModel.AddVariableTitle);
        Assert.Equal("Tip: paste a README snippet or an mcpServers stanza — a wrapper or a bare \"name\": {…} entry is unwrapped automatically, and the name filled in.", EditorModel.JsonTip);
    }

    [Fact]
    public void UrlHintShowsOnlyForANonEmptyInvalidUrl()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        editor.RemoteUrl = "ftp://x";
        Assert.True(editor.ShowUrlHint);
        Assert.False(editor.CanSave);
        editor.RemoteUrl = "https://x.example/mcp";
        Assert.False(editor.ShowUrlHint);
        Assert.True(editor.CanSave);
    }

    [Fact]
    public void PastedAuthConfigOnANewRemoteTargetKeepsItsLaunchStyleAndAuth()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var editor = Editor(h, state, EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        editor.RequestView(EditView.Json);
        var pasted = RemotePattern.Encode(new RemoteConfig(Url, new RemoteAuth.Header("X-API-Key", "v"), RemoteLaunchStyle.Npx, ["--transport", "sse-only"]));
        editor.JsonText = pasted.EditorText();
        editor.Name = "pasted";
        editor.RequestView(EditView.Form);
        Assert.True(editor.IsRemote);            // forcesRemote + isRemoteShaped
        Assert.Equal("", editor.RemoteUrl);      // catalog §3.5 quirk: adoptForm only takes the URL from detect()
        Assert.Equal(RemoteAuthKind.Header, editor.AuthKind);
        Assert.Equal("X-API-Key", editor.HeaderName);
        editor.RemoteUrl = Url;
        Assert.True(editor.Save());
        Assert.Equal(pasted, state.Store.Mcps["pasted"].Config);   // bare npx style and the extra args survive
    }

    [Fact]
    public void AdditionalKeysAreMergedOnARemoteSave()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var config = Local("npx", ["-y", "mcp-remote", Url], null, [("disabled", JsonValue.Bool(true))]);
        var editor = Editor(h, state, EditTarget.Existing("scoutbook", new McpEntry(config)));
        Assert.True(editor.IsRemote);
        Assert.True(editor.HasAdditional);
        editor.RemoteUrl = "https://moved.example/mcp";
        Assert.True(editor.Save());
        Assert.Equal(JsonValue.Bool(true), state.Store.Mcps["scoutbook"].Config["disabled"]);
    }
}

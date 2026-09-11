using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

public class EditorModelSaveTests
{
    private const string Url = "https://scoutbook.example.com/mcp";

    [Fact]
    public void SaveWithUnrecoverableJsonWritesNothing()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.New(rig.Local("node", ["x.js"])));
        editor.Name = "broken";
        editor.RequestView(EditView.Json);
        editor.JsonText = "{\"command\": ";
        Assert.False(editor.Save());
        Assert.False(rig.State.Store.Mcps.ContainsKey("broken"));
        Assert.Empty(rig.H.Dialogs.Confirms);
        Assert.Empty(rig.H.Dialogs.Informs);
    }

    /// <summary>The reset-to-bridge-invocation branch is new-target-only, so an existing connector's
    /// command/args survive a switch to Local.</summary>

    [Fact]
    public void JsonPasteFillsTheNameWhenBlankAndCanonicalizesTheText()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        editor.RequestView(EditView.Json);
        editor.JsonText = "{\"mcpServers\": {\"pasted\": {\"command\": \"node\", \"args\": [\"x.js\"]}}}";
        Assert.True(editor.Save());
        Assert.Equal("pasted", editor.Name);
        Assert.Equal("{\n  \"args\" : [\n    \"x.js\"\n  ],\n  \"command\" : \"node\"\n}", editor.JsonText);
        Assert.Equal(EditView.Json, rig.State.Store.Mcps["pasted"].LastEditView);
    }

    [Fact]
    public void AuthKindPickerOrder()
    {
        // The picker's case order: behavior StringCatalogTests doesn't cover
        // (it pins each case's title, not AuthKinds' order).
        Assert.Equal([RemoteAuthKind.Automatic, RemoteAuthKind.Bearer, RemoteAuthKind.Header, RemoteAuthKind.OAuthClient], EditorModel.AuthKinds);
    }

    /// <summary>Names are kept as typed: trimming once silently renamed a user's keys.</summary>
    [Fact]
    public void EnvNamesReachTheStoreVerbatim()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.New(rig.Local("node", ["server.js"])));
        editor.Name = "spaced";
        editor.AddEnvRow();
        editor.EnvRows[0].Name = " K ";
        editor.EnvRows[0].Value = "v";
        Assert.True(editor.Save());
        Assert.Equal(rig.Local("node", ["server.js"], [(" K ", "v")]), rig.State.Store.Mcps["spaced"].Config);
    }

    [Theory]
    [InlineData(RemoteAuthKind.Automatic, "", "", "", "", "Server URL must be a valid http(s) URL.")]
    [InlineData(RemoteAuthKind.Bearer, Url, "", "", "", "Enter a bearer token.")]
    [InlineData(RemoteAuthKind.Header, Url, "", "", "", "Enter a header name.")]
    [InlineData(RemoteAuthKind.Header, Url, "", "X-API-Key", "", "Enter a header value.")]
    [InlineData(RemoteAuthKind.OAuthClient, Url, "", "", "", "Enter a client ID.")]
    public void SaveValidatesTheRemoteForm(RemoteAuthKind kind, string url, string token, string headerName, string headerValue, string expected)
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        editor.Name = "r";
        editor.RemoteUrl = url;
        editor.AuthKindIndex = EditorRig.AuthKindIndexOf(kind);
        editor.BearerToken = token;
        editor.HeaderName = headerName;
        editor.HeaderValue = headerValue;
        Assert.False(editor.Save());
        Assert.Equal(expected, editor.ValidationError);
        Assert.False(rig.State.Store.Mcps.ContainsKey("r"));
    }

    [Fact]
    public void SaveValidatesTheLocalForm()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.New(rig.Local("", [])));
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
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.New(rig.Local("node", [])));
        editor.RequestView(EditView.Json);
        editor.JsonText = "{\"command\": \"npx\", \"args\": [\"-y\", \"mcp-remote\", \"nope\"]}";
        editor.Name = "bad";
        Assert.False(editor.Save());
        Assert.Equal("Server URL must be a valid http(s) URL.", editor.ValidationError);
    }

    [Fact]
    public void SaveNewRemoteWritesTheCmdNpxShapeAndAppliesImmediately()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        var closed = 0;
        editor.CloseRequested += () => closed++;
        editor.Name = "new-remote";
        editor.RemoteUrl = "https://new.example/mcp";
        Assert.True(editor.Save());
        Assert.Equal(1, closed);
        var entry = rig.State.Store.Mcps["new-remote"];
        Assert.True(entry.Enabled);
        Assert.Equal(EditView.Form, entry.LastEditView);
        Assert.Equal(rig.Local("cmd", ["/c", "npx", "-y", "mcp-remote", "https://new.example/mcp"]), entry.Config);
        Assert.True(rig.H.ClaudeServers().ContainsKey("new-remote"));
        Assert.Equal(rig.H.Now, rig.H.Settings.LastApplyDate);
    }

    [Theory]
    [InlineData(RemoteAuthKind.Bearer)]
    [InlineData(RemoteAuthKind.Header)]
    [InlineData(RemoteAuthKind.OAuthClient)]
    public void SaveEncodesEachAuthKind(RemoteAuthKind kind)
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        editor.Name = "auth";
        editor.RemoteUrl = Url;
        editor.AuthKindIndex = EditorRig.AuthKindIndexOf(kind);
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
        Assert.Equal(RemotePattern.Encode(new RemoteConfig(Url, expected, RemoteLaunchStyle.CmdNpx)), rig.State.Store.Mcps["auth"].Config);
    }

    /// <summary>An out-of-range index (a ComboBox cleared to -1) leaves AuthKind untouched but
    /// still raises PropertyChanged so the control snaps back to the current selection.</summary>

    [Fact]
    public void SaveExistingPreservesTheEnabledStateAndRecordsTheView()
    {
        using var rig = new EditorRig();
        rig.State.SetEnabled("scoutbook", false);
        var editor = rig.Editor(EditTarget.Existing("scoutbook", rig.State.Store.Mcps["scoutbook"]));
        editor.RemoteUrl = "https://moved.example/mcp";
        editor.RequestView(EditView.Json);
        Assert.True(editor.Save());
        var entry = rig.State.Store.Mcps["scoutbook"];
        Assert.False(entry.Enabled);
        Assert.Equal(EditView.Json, entry.LastEditView);
        Assert.Equal(rig.Local("npx", ["-y", "mcp-remote", "https://moved.example/mcp"]), entry.Config);   // decoded as bare npx, re-encoded as bare npx
        Assert.False(rig.H.ClaudeServers().ContainsKey("scoutbook"));   // disabled: not applied to Claude
    }

    [Fact]
    public void EditingAPinnedConnectorKeepsItsPin()
    {
        using var rig = new EditorRig();
        var pinned = rig.Local("npx", ["-y", "mcp-remote@0.1.16", Url]);
        Assert.Null(rig.State.Upsert("pinned", new McpEntry(pinned), null));
        var editor = rig.Editor(EditTarget.Existing("pinned", rig.State.Store.Mcps["pinned"]));
        editor.RemoteUrl = "https://moved.example/mcp";
        Assert.True(editor.Save());
        Assert.Equal(rig.Local("npx", ["-y", "mcp-remote@0.1.16", "https://moved.example/mcp"]), rig.State.Store.Mcps["pinned"].Config);

        var again = rig.Editor(EditTarget.Existing("pinned", rig.State.Store.Mcps["pinned"]));
        again.RequestView(EditView.Json);
        Assert.Contains("mcp-remote@0.1.16", again.JsonText);
    }

    [Fact]
    public void SaveRenameRemovesTheOldKeyAndNameErrorsSurface()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.Existing("scoutbook", rig.State.Store.Mcps["scoutbook"]));
        editor.Name = "aws-mcp";
        Assert.False(editor.Save());
        Assert.Equal("A connector named “aws-mcp” already exists.", editor.ValidationError);
        editor.Name = " ";
        Assert.False(editor.Save());
        Assert.Equal("Name must not be empty.", editor.ValidationError);
        editor.Name = "scoutbook2";
        Assert.True(editor.Save());
        Assert.False(rig.State.Store.Mcps.ContainsKey("scoutbook"));
        Assert.True(rig.State.Store.Mcps.ContainsKey("scoutbook2"));
        Assert.True(rig.H.ClaudeServers().ContainsKey("scoutbook2"));
    }

    [Fact]
    public void SaveConflictWhenTheEntryChangedOutsideTheEditor()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.Existing("scoutbook", rig.State.Store.Mcps["scoutbook"]));
        rig.State.Upsert("scoutbook", new McpEntry(AppStateHarness.Remote("https://elsewhere.example/mcp")), "scoutbook");
        editor.RemoteUrl = "https://mine.example/mcp";
        rig.H.Dialogs.NextConfirm = false;
        Assert.False(editor.Save());
        var call = Assert.Single(rig.H.Dialogs.Confirms);
        Assert.Equal(new FakeDialogs.ConfirmCall("“scoutbook” changed outside this editor.", "Saving will overwrite that change with this editor's version.", "Save Anyway", "Cancel", false), call);
        Assert.Equal(AppStateHarness.Remote("https://elsewhere.example/mcp"), rig.State.Store.Mcps["scoutbook"].Config);

        rig.H.Dialogs.NextConfirm = true;
        Assert.True(editor.Save());
        Assert.Equal(rig.Local("npx", ["-y", "mcp-remote", "https://mine.example/mcp"]), rig.State.Store.Mcps["scoutbook"].Config);
    }

    [Fact]
    public void SaveConflictWhenTheEntryWasRemovedOutsideTheEditor()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.Existing("scoutbook", rig.State.Store.Mcps["scoutbook"]));
        rig.State.Remove("scoutbook");
        Assert.True(editor.Save());
        Assert.Equal(new FakeDialogs.ConfirmCall("“scoutbook” was removed outside this editor.", "Saving will add it back.", "Save Anyway", "Cancel", false), rig.H.Dialogs.Confirms[0]);
        Assert.True(rig.State.Store.Mcps["scoutbook"].Enabled);   // a re-added entry takes the editor's snapshot enabled state
    }

    [Fact]
    public void RemoveConfirmsThenRemovesAndAppliesInOneTurn()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.Existing("scoutbook", rig.State.Store.Mcps["scoutbook"]));
        var closed = 0;
        editor.CloseRequested += () => closed++;
        rig.H.Dialogs.NextConfirm = false;
        editor.Remove();
        Assert.Equal(new FakeDialogs.ConfirmCall("Remove “scoutbook”? A copy remains in Backups.", null, "Remove", "Cancel", true), rig.H.Dialogs.Confirms[0]);
        Assert.True(rig.State.Store.Mcps.ContainsKey("scoutbook"));
        Assert.Equal(0, closed);

        rig.H.Dialogs.NextConfirm = true;
        editor.Remove();
        Assert.False(rig.State.Store.Mcps.ContainsKey("scoutbook"));
        Assert.False(rig.H.ClaudeServers().ContainsKey("scoutbook"));
        Assert.Equal(1, closed);
    }

    [Fact]
    public void AdditionalKeysAreMergedOnARemoteSave()
    {
        using var rig = new EditorRig();
        var config = rig.Local("npx", ["-y", "mcp-remote", Url], null, [("disabled", JsonValue.Bool(true))]);
        var editor = rig.Editor(EditTarget.Existing("scoutbook", new McpEntry(config)));
        Assert.True(editor.IsRemote);
        Assert.True(editor.HasAdditional);
        editor.RemoteUrl = "https://moved.example/mcp";
        Assert.True(editor.Save());
        Assert.Equal(JsonValue.Bool(true), rig.State.Store.Mcps["scoutbook"].Config["disabled"]);
    }

    [Fact]
    public void CancelClosesWithoutPersisting()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.Existing("scoutbook", rig.State.Store.Mcps["scoutbook"]));
        var closed = 0;
        editor.CloseRequested += () => closed++;
        editor.RemoteUrl = "https://edited.example/mcp";
        editor.Cancel();
        Assert.Equal(1, closed);
        Assert.Equal(Url, RemotePattern.Detect(rig.State.Store.Mcps["scoutbook"].Config));
    }
}

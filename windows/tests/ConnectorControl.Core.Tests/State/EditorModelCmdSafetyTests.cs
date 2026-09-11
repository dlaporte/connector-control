using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

public class EditorModelCmdSafetyTests
{
    private const string Url = "https://scoutbook.example.com/mcp";

    [Fact]
    public void CmdLauncherRefusesAUrlCmdWouldSplit()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        editor.Name = "r";
        editor.RemoteUrl = "https://127.0.0.1:1/mcp&ver";
        Assert.True(editor.RemoteUrlValid, "URL syntax alone does not catch it");
        Assert.False(editor.RemoteUrlCmdSafe);
        Assert.False(editor.CanSave);
        Assert.Equal(EditorModel.CmdUnsafeError("Server URL"), editor.UrlCaution);
        Assert.True(editor.ShowUrlCaution);
        Assert.False(editor.Save());
        Assert.Equal(EditorModel.CmdUnsafeError("Server URL"), editor.ValidationError);
        Assert.False(rig.State.Store.Mcps.ContainsKey("r"));
    }

    [Fact]
    public void CmdLauncherCautionsAboutPercentExpansionButSaves()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        editor.Name = "r";
        editor.RemoteUrl = "https://x.dev/%41%42";
        Assert.True(editor.RemoteUrlCmdSafe);
        Assert.True(editor.CanSave);
        Assert.Equal(EditorModel.CmdPercentCaution, editor.UrlCaution);
        Assert.True(editor.Save());
        Assert.True(rig.State.Store.Mcps.ContainsKey("r"));
    }

    [Fact]
    public void BareNpxLauncherAcceptsTheSameUrl()
    {
        // A synced Mac entry keeps its bare-npx style; cross-spawn escapes npx.cmd's own arguments.
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.Existing("scoutbook", rig.State.Store.Mcps["scoutbook"]));
        editor.RemoteUrl = "https://x.dev/mcp?a=b&c=d";
        Assert.True(editor.RemoteUrlCmdSafe);
        Assert.True(editor.CanSave);
        Assert.Null(editor.UrlCaution);
        Assert.False(editor.ShowUrlCaution);
        Assert.True(editor.Save());
        Assert.Equal(RemotePattern.Make("https://x.dev/mcp?a=b&c=d", RemoteLaunchStyle.Npx), rig.State.Store.Mcps["scoutbook"].Config);
    }

    [Theory]
    [InlineData(RemoteAuthKind.Header, "X-Key&calc", "v", "", "", "", "Header name")]
    [InlineData(RemoteAuthKind.OAuthClient, "", "", "id|calc", "s", "", "Client ID")]
    [InlineData(RemoteAuthKind.OAuthClient, "", "", "id", "s^calc", "", "Client Secret")]
    [InlineData(RemoteAuthKind.OAuthClient, "", "", "id", "s", "openid&calc", "Scopes")]
    public void CmdLauncherRefusesAuthFieldsCmdWouldReparse(RemoteAuthKind kind, string headerName, string headerValue, string clientId, string clientSecret, string scopes, string field)
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        editor.Name = "r";
        editor.RemoteUrl = Url;
        editor.AuthKindIndex = EditorRig.AuthKindIndexOf(kind);
        editor.HeaderName = headerName;
        editor.HeaderValue = headerValue;
        editor.OAuthClientId = clientId;
        editor.OAuthClientSecret = clientSecret;
        editor.OAuthScopes = scopes;
        Assert.False(editor.Save());
        Assert.Equal(EditorModel.CmdUnsafeError(field), editor.ValidationError);
        Assert.False(rig.State.Store.Mcps.ContainsKey("r"));
    }

    [Fact]
    public void CmdLauncherLeavesEnvOnlyFieldsAlone()
    {
        // Bearer tokens and header VALUES travel in env (AUTH_HEADER), which cmd.exe never parses.
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        editor.Name = "r";
        editor.RemoteUrl = Url;
        editor.AuthKindIndex = EditorRig.AuthKindIndexOf(RemoteAuthKind.Header);
        editor.HeaderName = "X-API-Key";
        editor.HeaderValue = "a&b|c";
        Assert.True(editor.Save());
        Assert.Equal("a&b|c", rig.State.Store.Mcps["r"].Config["env"]!["AUTH_HEADER"]!.StringValue);
    }

    [Fact]
    public void CmdLauncherAllowsSpaceSeparatedScopes()
    {
        // Scopes are a space-separated list by definition; the whitespace allowance is only for them.
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        editor.Name = "r";
        editor.RemoteUrl = Url;
        editor.AuthKindIndex = EditorRig.AuthKindIndexOf(RemoteAuthKind.OAuthClient);
        editor.OAuthClientId = "id";
        editor.OAuthClientSecret = "s";
        editor.OAuthScopes = "openid profile";
        Assert.True(editor.Save());
        var args = rig.State.Store.Mcps["r"].Config["args"]!.ArrayItems.Select(a => a.StringValue).ToArray();
        Assert.Contains("{\"scope\":\"openid profile\"}", args);
    }
}

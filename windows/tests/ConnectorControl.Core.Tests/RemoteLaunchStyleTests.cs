using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

public class RemoteLaunchStyleTests
{
    private const string Url = "https://mcp.example.com/sse";

    private static JsonValue CmdConfig(params string[] argsAfterNpx) => JsonValue.Object(
        ("command", JsonValue.String("cmd")),
        ("args", JsonValue.Array(new[] { "/c", "npx" }.Concat(argsAfterNpx).Select(JsonValue.String))));

    [Fact]
    public void FixtureIsDetectedAsRemote()
    {
        var config = JsonValue.Parse(Fixtures.Bytes("remote_cmd_npx.json"));
        Assert.Equal(Url, RemotePattern.Detect(config));
        Assert.True(RemotePattern.IsRemoteShaped(config));
        Assert.True(RemotePattern.IsCanonicalShape(config));
    }

    [Fact]
    public void MakeWithCmdNpxStyle()
    {
        Assert.Equal(CmdConfig("-y", "mcp-remote", Url), RemotePattern.Make(Url, RemoteLaunchStyle.CmdNpx));
        Assert.Equal(Url, RemotePattern.Detect(RemotePattern.Make(Url, RemoteLaunchStyle.CmdNpx)));
    }

    [Theory]
    [InlineData("cmd", "/c")]
    [InlineData("CMD", "/C")]
    [InlineData("cmd.exe", "/c")]
    [InlineData("Cmd.EXE", "/C")]
    public void LauncherSpellingsAreCaseInsensitive(string command, string flag)
    {
        var config = JsonValue.Object(("command", JsonValue.String(command)),
            ("args", JsonValue.Array([JsonValue.String(flag), JsonValue.String("npx"), JsonValue.String("-y"), JsonValue.String("mcp-remote"), JsonValue.String(Url)])));
        Assert.Equal(Url, RemotePattern.Detect(config));
    }

    [Fact]
    public void CmdWithoutNpxIsNotRemote()
    {
        var config = JsonValue.Object(("command", JsonValue.String("cmd")),
            ("args", JsonValue.Array([JsonValue.String("/c"), JsonValue.String("node"), JsonValue.String("mcp-remote"), JsonValue.String(Url)])));
        Assert.Null(RemotePattern.Detect(config));
        Assert.False(RemotePattern.IsRemoteShaped(config));
        Assert.Null(RemotePattern.Decode(config));
    }

    [Fact]
    public void DecodeRecordsTheStyleAndEncodeKeepsIt()
    {
        var config = CmdConfig("-y", "mcp-remote", Url, "--header", "Authorization:${AUTH_HEADER}")
            .With("env", JsonValue.Object(("AUTH_HEADER", JsonValue.String("Bearer t"))));
        var decoded = RemotePattern.Decode(config);
        Assert.NotNull(decoded);
        Assert.Equal(RemoteLaunchStyle.CmdNpx, decoded.LaunchStyle);
        Assert.Equal(new RemoteAuth.Bearer("t"), decoded.Auth);
        Assert.Equal(config, RemotePattern.Encode(decoded));
    }

    [Fact]
    public void BareNpxDecodesAsNpxStyle()
    {
        var decoded = RemotePattern.Decode(RemotePattern.Make(Url, RemoteLaunchStyle.Npx));
        Assert.Equal(RemoteLaunchStyle.Npx, decoded?.LaunchStyle);
    }

    [Fact]
    public void StyleIsPartOfEquality()
    {
        Assert.NotEqual(new RemoteConfig(Url, RemoteAuth.Auto, RemoteLaunchStyle.Npx), new RemoteConfig(Url, RemoteAuth.Auto, RemoteLaunchStyle.CmdNpx));
    }
}

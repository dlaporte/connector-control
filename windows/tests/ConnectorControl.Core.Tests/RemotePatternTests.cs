namespace ConnectorControl.Core.Tests;

public class RemotePatternTests
{
    private static JsonValue Config(string[] args, string command = "npx") =>
        JsonValue.Object(("command", JsonValue.String(command)), ("args", JsonValue.Array(args.Select(JsonValue.String))));

    [Fact]
    public void DetectsCanonicalPattern()
    {
        Assert.Equal("https://example.com/mcp", RemotePattern.Detect(Config(["-y", "mcp-remote", "https://example.com/mcp"])));
    }

    [Fact]
    public void DetectsPatternWithoutDashY()
    {
        Assert.Equal("https://x.dev/mcp", RemotePattern.Detect(Config(["mcp-remote", "https://x.dev/mcp"])));
    }

    [Fact]
    public void ExtraKeysDoNotDisqualify()
    {
        var value = JsonValue.Object(
            ("command", JsonValue.String("npx")),
            ("args", JsonValue.Array([JsonValue.String("-y"), JsonValue.String("mcp-remote"), JsonValue.String("https://x.dev/mcp")])),
            ("env", JsonValue.Object(("TOKEN", JsonValue.String("abc")))));
        Assert.Equal("https://x.dev/mcp", RemotePattern.Detect(value));
    }

    [Fact]
    public void RejectsWrongCommand()
    {
        Assert.Null(RemotePattern.Detect(Config(["-y", "mcp-remote", "https://x.dev/mcp"], command: "node")));
    }

    [Fact]
    public void RejectsExtraArgs()
    {
        Assert.Null(RemotePattern.Detect(Config(["-y", "mcp-remote", "https://x.dev/mcp", "--debug"])));
    }

    [Fact]
    public void RejectsNonUrl()
    {
        Assert.Null(RemotePattern.Detect(Config(["-y", "mcp-remote", "not a url"])));
        Assert.Null(RemotePattern.Detect(Config(["-y", "mcp-remote", "ftp://x.dev"])));
    }

    [Fact]
    public void RejectsMissingArgsOrNonStringArgs()
    {
        Assert.Null(RemotePattern.Detect(JsonValue.Object(("command", JsonValue.String("npx")))));
        Assert.Null(RemotePattern.Detect(JsonValue.Object(
            ("command", JsonValue.String("npx")),
            ("args", JsonValue.Array([JsonValue.String("mcp-remote"), JsonValue.Int(42)])))));
    }

    [Fact]
    public void MakeBuildsCanonicalConfig()
    {
        Assert.Equal(Config(["-y", "mcp-remote", "https://x.dev/mcp"]), RemotePattern.Make("https://x.dev/mcp"));
    }

    [Fact]
    public void MakeThenDetectRoundTrips()
    {
        Assert.Equal("https://x.dev/mcp", RemotePattern.Detect(RemotePattern.Make("https://x.dev/mcp")));
    }

    [Fact]
    public void IsRemoteShapedAcceptsInvalidUrl()
    {
        Assert.True(RemotePattern.IsRemoteShaped(Config(["-y", "mcp-remote", ""])));
        Assert.True(RemotePattern.IsRemoteShaped(Config(["mcp-remote", "not a url"])));
        Assert.True(RemotePattern.IsRemoteShaped(RemotePattern.Make("https://x.dev/mcp")));
    }

    [Fact]
    public void IsCanonicalShapeCoversBareInvocations()
    {
        Assert.True(RemotePattern.IsCanonicalShape(Config(["-y", "mcp-remote", "not a url"])));
        Assert.True(RemotePattern.IsCanonicalShape(RemotePattern.Make("https://x.dev/mcp")));
        Assert.True(RemotePattern.IsCanonicalShape(Config(["-y", "mcp-remote"])));
        Assert.False(RemotePattern.IsCanonicalShape(Config(["-y", "mcp-remote", "https://x.dev/mcp", "--header", "A: B"])));
        Assert.False(RemotePattern.IsCanonicalShape(Config(["-y", "pkg"])));
    }

    [Fact]
    public void IsRemoteShapedRejectsNonRemote()
    {
        Assert.False(RemotePattern.IsRemoteShaped(Config(["-y", "some-package"])));
        Assert.False(RemotePattern.IsRemoteShaped(Config(["mcp-remote", "https://x.dev"], command: "node")));
        Assert.False(RemotePattern.IsRemoteShaped(JsonValue.Object(("command", JsonValue.String("npx")))));
    }

    [Theory]
    [InlineData("https://example.com/mcp", true)]
    [InlineData("http://localhost:8080/sse", true)]
    [InlineData("HTTPS://X.DEV/mcp", true)]
    [InlineData("ftp://x.dev", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    [InlineData("https://", false)]
    public void IsValidHttpUrl(string url, bool expected)
    {
        Assert.Equal(expected, RemotePattern.IsValidHttpUrl(url));
    }
}

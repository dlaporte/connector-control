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
        Assert.Equal(Config(["-y", "mcp-remote", "https://x.dev/mcp"]), RemotePattern.Make("https://x.dev/mcp", RemoteLaunchStyle.Npx));
    }

    [Fact]
    public void MakeThenDetectRoundTrips()
    {
        Assert.Equal("https://x.dev/mcp", RemotePattern.Detect(RemotePattern.Make("https://x.dev/mcp", RemoteLaunchStyle.Npx)));
    }

    [Fact]
    public void IsRemoteShapedAcceptsInvalidUrl()
    {
        Assert.True(RemotePattern.IsRemoteShaped(Config(["-y", "mcp-remote", ""])));
        Assert.True(RemotePattern.IsRemoteShaped(Config(["mcp-remote", "not a url"])));
        Assert.True(RemotePattern.IsRemoteShaped(RemotePattern.Make("https://x.dev/mcp", RemoteLaunchStyle.Npx)));
    }

    [Fact]
    public void IsCanonicalShapeCoversBareInvocations()
    {
        Assert.True(RemotePattern.IsCanonicalShape(Config(["-y", "mcp-remote", "not a url"])));
        Assert.True(RemotePattern.IsCanonicalShape(RemotePattern.Make("https://x.dev/mcp", RemoteLaunchStyle.Npx)));
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

    [Fact]
    public void UrlSyntaxAloneDoesNotCatchCmdMetacharacters()
    {
        // The security-review case: valid by Uri.TryCreate, split by cmd.exe at the &.
        Assert.True(RemotePattern.IsValidHttpUrl("https://127.0.0.1:1/mcp&ver"));
        Assert.True(RemotePattern.IsValidHttpUrl("https://x.dev/mcp?a=b&c=d"));
    }

    [Theory]
    [InlineData("https://127.0.0.1:1/mcp&ver", '&')]
    [InlineData("https://x.dev/mcp?a=b&c=d", '&')]
    [InlineData("https://x.dev/mcp|calc", '|')]
    [InlineData("https://x.dev/mcp<x", '<')]
    [InlineData("https://x.dev/mcp>x", '>')]
    [InlineData("https://x.dev/mcp^x", '^')]
    [InlineData("https://x.dev/mcp\"x", '"')]
    [InlineData("https://x.dev/mcp x", ' ')]
    [InlineData("X-Key\tcalc", '\t')]
    public void CmdUnsafeCharacterFindsTheFirstOffender(string value, char expected)
    {
        Assert.Equal(expected, RemotePattern.CmdUnsafeCharacter(value));
    }

    [Theory]
    [InlineData("https://x.dev/mcp")]
    [InlineData("https://x.dev/mcp?a=b")]
    [InlineData("https://x.dev/a%20b")]
    [InlineData("X-API-Key")]
    [InlineData("client-id_123.ABC~")]
    [InlineData("")]
    public void CmdUnsafeCharacterIsNullForPlainValues(string value)
    {
        Assert.Null(RemotePattern.CmdUnsafeCharacter(value));
    }

    [Fact]
    public void WhitespaceCanBeAllowedForSpaceSeparatedFields()
    {
        // OAuth scopes are space-separated by definition; the metacharacters still count.
        Assert.Null(RemotePattern.CmdUnsafeCharacter("openid profile", allowWhitespace: true));
        Assert.Equal('&', RemotePattern.CmdUnsafeCharacter("openid&calc", allowWhitespace: true));
    }

    [Theory]
    [InlineData("https://x.dev/a%20b", false)]
    [InlineData("https://x.dev/%41%42", true)]
    [InlineData("https://x.dev/%COMSPEC%", true)]
    [InlineData("plain", false)]
    public void HasCmdExpansionRiskNeedsTwoPercentSigns(string value, bool expected)
    {
        Assert.Equal(expected, RemotePattern.HasCmdExpansionRisk(value));
    }

    private const string SafeUrl = "https://x.dev/mcp";

    public static TheoryData<RemoteConfig, RemoteField?> CmdUnsafeFields => new()
    {
        { new RemoteConfig("https://127.0.0.1:1/mcp&ver", RemoteAuth.Auto, RemoteLaunchStyle.CmdNpx), RemoteField.Url },
        { new RemoteConfig(SafeUrl, new RemoteAuth.Header("X-Key&calc", "v"), RemoteLaunchStyle.CmdNpx), RemoteField.HeaderName },
        { new RemoteConfig(SafeUrl, new RemoteAuth.OAuthClient("id|calc", "s", ""), RemoteLaunchStyle.CmdNpx), RemoteField.ClientId },
        { new RemoteConfig(SafeUrl, new RemoteAuth.OAuthClient("id", "s^calc", ""), RemoteLaunchStyle.CmdNpx), RemoteField.ClientSecret },
        { new RemoteConfig(SafeUrl, new RemoteAuth.OAuthClient("id", "s", "openid&calc"), RemoteLaunchStyle.CmdNpx), RemoteField.Scopes },
        { new RemoteConfig(SafeUrl, new RemoteAuth.OAuthClient("id", "s", "openid profile"), RemoteLaunchStyle.CmdNpx), null },   // scopes are space-separated by definition
        { new RemoteConfig(SafeUrl, new RemoteAuth.Bearer("tok&calc"), RemoteLaunchStyle.CmdNpx), null },                        // the token travels in env
        { new RemoteConfig(SafeUrl, new RemoteAuth.Header("X-Key", "v&calc"), RemoteLaunchStyle.CmdNpx), null },                 // so does a header VALUE
        { new RemoteConfig("https://127.0.0.1:1/mcp&ver", RemoteAuth.Auto, RemoteLaunchStyle.Npx), null },                       // bare npx: cross-spawn escapes for us
    };

    [Theory]
    [MemberData(nameof(CmdUnsafeFields))]
    public void CmdUnsafeFieldNamesTheFirstArgThatCmdWouldReparse(RemoteConfig config, RemoteField? expected)
    {
        Assert.Equal(expected, RemotePattern.CmdUnsafeField(config));
    }

    [Fact]
    public void DefaultPackageIsTheMarkerAndWhatMakeWrites()
    {
        Assert.True(RemotePattern.IsMarker(RemotePattern.DefaultPackage));
        Assert.Equal(RemotePattern.DefaultPackage, RemotePattern.Decode(RemotePattern.Make(SafeUrl, RemoteLaunchStyle.Npx))!.Package);
    }
}

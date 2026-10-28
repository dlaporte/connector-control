using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.State;

/// <summary>Mirror: Tests/ConnectorControlStateTests/ControlMaskingTests.swift</summary>
public class ControlMaskingTests
{
    private const string Token = "ghp_0123456789abcdefghijklmnopqrstuvwxyzAB";

    private static JsonValue S(string s) => JsonValue.String(s);

    private static readonly JsonValue Github = JsonValue.Object(
        ("command", S("npx")),
        ("args", JsonValue.Array([S("-y"), S("@modelcontextprotocol/server-github")])),
        ("env", JsonValue.Object(("GITHUB_TOKEN", S(Token)), ("EMPTY", S("")), ("LATER", S("${CC_NEEDS:LATER}")))));

    private static JsonValue Splunk(string url, string token) =>
        RemotePattern.Encode(new RemoteConfig(url, new RemoteAuth.Bearer(token), RemoteLaunchStyle.Npx, [],
            new Dictionary<string, string>(), RemotePattern.DefaultPackage));

    [Fact]
    public void EnvironmentValuesReadAsSetOrNeedsValue()
    {
        Assert.Equal(
            JsonValue.Object(
                ("command", S("npx")),
                ("args", JsonValue.Array([S("-y"), S("@modelcontextprotocol/server-github")])),
                ("env", JsonValue.Object(("GITHUB_TOKEN", S("set")), ("EMPTY", S("needs value")), ("LATER", S("needs value"))))),
            ControlMasking.MaskedConfig(Github));
    }

    [Fact]
    public void ArgumentsThatReadAsCredentialsAreMasked()
    {
        Assert.Equal(
            ["--token", "•••", "--api-key=•••", "•••", "/Users/x/server.js", "${CC_NEEDS:server_path}",
             "${COLLECTION_DIR}/srv.js", "--port", "8080"],
            ControlMasking.Arguments(
                ["--token", "abc", "--api-key=sk-123", Token, "/Users/x/server.js", "${CC_NEEDS:server_path}",
                 "${COLLECTION_DIR}/srv.js", "--port", "8080"]));
    }

    [Fact]
    public void AUrlKeepsItsHostAndPathAndMasksItsPasswordAndQueryValues()
    {
        Assert.Equal("https://user:•••@example.com/mcp?api_key=•••&region=•••#top",
            ControlMasking.Url("https://user:hunter2@example.com/mcp?api_key=abc&region=us#top"));
        Assert.Equal("https://example.com/mcp?token=${CC_NEEDS:token}",
            ControlMasking.Url("https://example.com/mcp?token=${CC_NEEDS:token}"));
        Assert.Equal("https://example.com/mcp", ControlMasking.Url("https://example.com/mcp"));
        Assert.Equal("not a url", ControlMasking.Url("not a url"));
    }

    [Fact]
    public void AHeadersValueIsMaskedUnlessItRefersElsewhere()
    {
        Assert.Equal(
            ["-y", "mcp-remote", "https://x.example/mcp?k=•••",
             "--header", "Authorization:${AUTH_HEADER}", "--header", "X-Team: •••"],
            ControlMasking.Arguments(["-y", "mcp-remote", "https://x.example/mcp?k=v",
                "--header", "Authorization:${AUTH_HEADER}", "--header", "X-Team: blue"]));
    }

    [Fact]
    public void TheOAuthClientInfoAfterItsFlagIsMasked()
    {
        Assert.Equal(["--static-oauth-client-info", "•••"],
            ControlMasking.Arguments(["--static-oauth-client-info", "{\"client_id\":\"a\",\"client_secret\":\"b\"}"]));
    }

    [Fact]
    public void CodeLinesForALocalConnector()
    {
        Assert.Equal(
            [
                "command   npx",
                "args      -y @modelcontextprotocol/server-github",
                "env       EMPTY = needs your value",
                "          GITHUB_TOKEN = ••• (set)",
                "          LATER = needs your value",
            ],
            ControlMasking.CodeLines(Github));
    }

    [Fact]
    public void CodeLinesForARemoteConnectorNameItsBridgeUrlAndHeader()
    {
        Assert.Equal(
            [
                "type      remote, through mcp-remote",
                "url       https://harvard.splunkcloud.com/mcp?tenant=•••",
                "headers   Authorization = ••• (set)",
            ],
            ControlMasking.CodeLines(Splunk("https://harvard.splunkcloud.com/mcp?tenant=acme", "s3cr3t")));
    }

    [Fact]
    public void RevealShowsEverythingAsWritten()
    {
        Assert.Equal(Github, ControlMasking.MaskedConfig(Github, reveal: true));
        Assert.Contains($"          GITHUB_TOKEN = {Token}", ControlMasking.CodeLines(Github, reveal: true));
        Assert.Equal("https://u:p@h.example/?a=b", ControlMasking.Url("https://u:p@h.example/?a=b", reveal: true));
    }

    [Fact]
    public void ChangeLinesShowOnlyWhatDiffersAndAChangedSecretAsChanged()
    {
        var before = Splunk("https://a.example/mcp", "one");
        var after = Splunk("https://b.example/mcp", "two");
        Assert.Equal(
            [
                "url",
                "  − https://a.example/mcp",
                "  + https://b.example/mcp",
                "headers",
                "  − Authorization = ••• (set)",
                "  + Authorization = ••• (set)",
            ],
            ControlMasking.ChangeLines(before, after));
        Assert.Empty(ControlMasking.ChangeLines(before, before));
    }

    [Fact]
    public void NoSecretSurvivesMasking()
    {
        foreach (var config in new[] { Github, Splunk("https://h.example/mcp?key=k3y", "s3cr3t") })
        {
            var text = System.Text.Encoding.UTF8.GetString(ControlMasking.MaskedConfig(config).Serialize())
                + string.Concat(ControlMasking.CodeLines(config));
            foreach (var secret in new[] { Token, "s3cr3t", "k3y" })
            {
                Assert.DoesNotContain(secret, text);
            }
        }
    }

    /// <summary>Every masked view of <paramref name="config"/> — its structured form, its code lines and its change lines
    /// from nothing — without any of <paramref name="secrets"/>.</summary>
    private static void AssertHidden(string[] secrets, JsonValue config)
    {
        var structured = System.Text.Encoding.UTF8.GetString(ControlMasking.MaskedConfig(config).Serialize());
        var text = string.Join("\n", ControlMasking.CodeLines(config).Concat(ControlMasking.ChangeLines(JsonValue.Object(), config)));
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, structured);
            Assert.DoesNotContain(secret, text);
        }
    }

    private static JsonValue Local(string command, string[] args) =>
        JsonValue.Object(("command", S(command)), ("args", JsonValue.Array(args.Select(S))));

    [Fact]
    public void APlaceholderInsideAnArgumentLeavesTheRestMasked()
    {
        string[] args = ["-y", "mcp-remote", "https://h.example/mcp?api_key=sk-live-1234&user=${CC_NEEDS:user}"];
        Assert.Equal(["-y", "mcp-remote", "https://h.example/mcp?api_key=•••&user=${CC_NEEDS:user}"],
            ControlMasking.Arguments(args));
        Assert.Equal("https://user:•••@h.example/${CC_NEEDS:path}",
            ControlMasking.Argument("https://user:hunter2@h.example/${CC_NEEDS:path}"));
        Assert.Equal("--api-key=•••", ControlMasking.Argument("--api-key=sk-123${CC_NEEDS:rest}"));
        Assert.Equal("•••", ControlMasking.Argument("sk-live-1234${CC_NEEDS:rest}"));
        Assert.Equal("${CC_NEEDS:server_path}", ControlMasking.Argument("${CC_NEEDS:server_path}"));
        AssertHidden(["sk-live-1234"], Local("npx", args));
        AssertHidden(["hunter2"], Local("node", ["s.js", "https://user:hunter2@h.example/${CC_NEEDS:path}"]));
        AssertHidden(["sk-123", "sk-live-1234"],
            Local("node", ["--api-key=sk-123${CC_NEEDS:rest}", "sk-live-1234${CC_NEEDS:rest}"]));
    }

    [Fact]
    public void AUrlMasksACredentialInItsUserNamePathOrFragment()
    {
        const string zapier = "https://actions.zapier.com/mcp/sk-ak-ZjQ2YTk1NzQtYmM0Ny00/sse";
        const string user = "https://ghp_0123456789abcdefghijklmnopqrstuvwxAB@api.example.com/mcp";
        const string basic = "https://ghp_0123456789abcdefghijklmnopqrstuvwxAB:x-oauth-basic@api.example.com/mcp";
        const string fragment = "https://h.example/cb#access_token=hunter2";
        Assert.Equal("https://actions.zapier.com/mcp/•••/sse", ControlMasking.Url(zapier));
        Assert.Equal("https://•••@api.example.com/mcp", ControlMasking.Url(user));
        Assert.Equal("https://•••:•••@api.example.com/mcp", ControlMasking.Url(basic));
        Assert.Equal("https://h.example/cb#access_token=•••", ControlMasking.Url(fragment));
        Assert.Equal("https://bob@h.example/a/b/#top", ControlMasking.Url("https://bob@h.example/a/b/#top"));
        AssertHidden(["sk-ak-ZjQ2YTk1NzQtYmM0Ny00"], JsonValue.Object(("url", S(zapier))));
        AssertHidden(["ghp_0123456789abcdefghijklmnopqrstuvwxAB", "hunter2"], Local("node", ["s.js", user, basic, fragment]));
    }

    [Fact]
    public void ANameEqualsValueWordNamedForASecretIsMasked()
    {
        string[] docker = ["run", "-i", "--rm", "-e", "DB_PASSWORD=hunter2", "-e", "API_KEY=abc-123", "img"];
        Assert.Equal(["run", "-i", "--rm", "-e", "DB_PASSWORD=•••", "-e", "API_KEY=•••", "img"], ControlMasking.Arguments(docker));
        Assert.Equal(["API_KEY=•••", "node", "s.js"], ControlMasking.Arguments(["API_KEY=hunter2", "node", "s.js"]));
        Assert.Equal("•••", ControlMasking.Argument("apikeyQ2xpZW50U2VjcmV0MTIzNDU2Nzg5MA=="));
        AssertHidden(["hunter2", "abc-123"], Local("docker", docker));
        AssertHidden(["hunter2"], Local("env", ["API_KEY=hunter2", "node", "s.js"]));
    }

    [Fact]
    public void AValueUnderAKeyNamedForASecretIsMasked()
    {
        var oauth = JsonValue.Object(
            ("url", S("https://h.example/mcp")),
            ("oauth", JsonValue.Object(("clientId", S("a")), ("clientSecret", S("hunter2")))));
        Assert.Equal(
            JsonValue.Object(
                ("url", S("https://h.example/mcp")),
                ("oauth", JsonValue.Object(("clientId", S("set")), ("clientSecret", S("set"))))),
            ControlMasking.MaskedConfig(oauth));
        var key = JsonValue.Object(("command", S("node")), ("args", JsonValue.Array([S("s.js")])), ("apiKey", S("hunter2")));
        Assert.Equal(
            JsonValue.Object(("command", S("node")), ("args", JsonValue.Array([S("s.js")])), ("apiKey", S("set"))),
            ControlMasking.MaskedConfig(key));
        AssertHidden(["hunter2"], oauth);
        AssertHidden(["hunter2"], key);
        AssertHidden(["hunter2"], JsonValue.Object(("command", S("node")), ("args", JsonValue.Array([S("--token"), S("hunter2"), JsonValue.Int(1)]))));
    }

    [Fact]
    public void AFlagAfterASecretNamedFlagIsNotTakenForItsValue()
    {
        Assert.Equal(["--no-auth", "--allow-http", "--bypass-sandbox", "--yolo", "--token", "•••"],
            ControlMasking.Arguments(["--no-auth", "--allow-http", "--bypass-sandbox", "--yolo", "--token", "abc"]));
    }

    /// <summary>The Mac mirror cuts at a delimiter scalar by scalar; a combining mark after the delimiter
    /// must not move the cut, or the two platforms show the same text differently.</summary>
    [Fact]
    public void ACombiningMarkAfterADelimiterCutsTheSameOnBothPlatforms()
    {
        Assert.Equal("https://u:•••@́h.example/", ControlMasking.Url("https://u:hunter2@́h.example/"));
        Assert.Equal("https://h.example/?a=•••&́c=•••", ControlMasking.Url("https://h.example/?a=b&́c=hunter2"));
        Assert.Equal("--api-key=•••", ControlMasking.Argument("--api-key=́hunter2"));
        Assert.Equal("-́-api-key=•••", ControlMasking.Argument("-́-api-key=hunter2"));
        Assert.Equal(["--header", "X-Key: •••"], ControlMasking.Arguments(["--header", "X-Key:́hunter2"]));
    }

    [Fact]
    public void NoSecretSurvivesInArgumentsUrlsOrChanges()
    {
        var shown = string.Join("\n",
            ControlMasking.Arguments(["--token", "abc9", "--api-key=sk-123x", Token, "--header", "X-Team: blue7"])
                .Append(ControlMasking.Url("https://user:hunter2@example.com/mcp?api_key=abc9&region=us7#top"))
                .Concat(ControlMasking.ChangeLines(Splunk("https://a.example/mcp?k=old1", "one1"),
                    Splunk("https://a.example/mcp?k=new2", "two2"))));
        foreach (var secret in new[] { Token, "abc9", "sk-123x", "blue7", "hunter2", "us7", "old1", "new2", "one1", "two2" })
        {
            Assert.DoesNotContain(secret, shown);
        }
    }
}

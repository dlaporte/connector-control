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

    private static readonly JsonValue Notion = JsonValue.Object(
        ("url", S("https://mcp.notion.com/mcp?x=1")),
        ("headers", JsonValue.Object(("Authorization", S("Bearer t0k")), ("X-Empty", S("")))));

    /// <summary>An mcp-remote config for https://h.example/mcp, written by hand: <c>bridge</c> before the URL,
    /// <c>args</c> after it.</summary>
    private static JsonValue Remote(string[] args, Dictionary<string, JsonValue>? env = null, string[]? bridge = null)
    {
        var config = JsonValue.Object(
            ("command", S("npx")),
            ("args", JsonValue.Array((bridge ?? ["-y", "mcp-remote"]).Concat(["https://h.example/mcp"]).Concat(args).Select(S))));
        return env is { Count: > 0 } ? config.With("env", JsonValue.Object(env)) : config;
    }

    private static JsonValue Local(string command, string[] args) =>
        JsonValue.Object(("command", S(command)), ("args", JsonValue.Array(args.Select(S))));

    private static JsonValue Adding(string key, JsonValue value, JsonValue config) => config.With(key, value);

    private static JsonValue OAuthClient(string url, string scopes) =>
        RemotePattern.Encode(new RemoteConfig(url, new RemoteAuth.OAuthClient("cid", "csec", scopes), RemoteLaunchStyle.Npx, [],
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
    public void CodeLinesForAConnectorReachedByUrlAlone()
    {
        Assert.Equal(
            [
                "type      remote",
                "url       https://mcp.notion.com/mcp?x=•••",
                "headers   Authorization = ••• (set)",
                "          X-Empty = needs your value",
            ],
            ControlMasking.CodeLines(Notion));
    }

    [Fact]
    public void CodeLinesForAnOAuthClientShowItsIdButNotItsSecret()
    {
        var config = OAuthClient("https://h.example/mcp", "read write");
        Assert.Equal(
            [
                "type     remote, through mcp-remote",
                "url      https://h.example/mcp",
                "oauth    client cid, secret ••• (set)",
                "scopes   read write",
            ],
            ControlMasking.CodeLines(config));
        Assert.Contains("oauth    client cid, secret csec", ControlMasking.CodeLines(config, ControlMasking.Exposure.Revealed));
    }

    [Fact]
    public void AHeaderAfterDashHIsMaskedAsAfterHeader()
    {
        Assert.Equal(["-H", "X-Api-Key: •••", "-H", "Authorization:${AUTH}"],
            ControlMasking.Arguments(["-H", "X-Api-Key: abc", "-H", "Authorization:${AUTH}"]));
    }

    [Fact]
    public void RevealShowsEverythingAsWritten()
    {
        Assert.Equal(Github, ControlMasking.MaskedConfig(Github, reveal: true));
        Assert.Contains($"          GITHUB_TOKEN = {Token}", ControlMasking.CodeLines(Github, ControlMasking.Exposure.Revealed));
        Assert.Equal("https://u:p@h.example/?a=b", ControlMasking.Url("https://u:p@h.example/?a=b", reveal: true));
        Assert.Equal(["--token", "abc", Token, "--header", "X-Key: v"],
            ControlMasking.Arguments(["--token", "abc", Token, "--header", "X-Key: v"], reveal: true));
    }

    /// <summary>The confirmation dialog, which nothing sends back to Claude, shows what would run in full:
    /// only environment and header values, the OAuth client secret and a key named for a secret
    /// stay masked.</summary>
    [Fact]
    public void TheDialogShowsTheCommandArgumentsAndUrlInFull()
    {
        var docker = JsonValue.Object(
            ("command", S("docker")),
            ("args", JsonValue.Array(new[]
            {
                "run", "-e", "API_KEY=hunter2", "--token", "abc", "https://u:pw@h.example/x?k=v",
                "--header", "X-Key: hval", "--static-oauth-client-info", """{"client_id":"a","client_secret":"csec"}""",
                "img",
            }.Select(S))),
            ("env", JsonValue.Object(("GITHUB_TOKEN", S(Token)))),
            ("apiKey", S("topsecret")));
        Assert.Equal(
            [
                "command   docker",
                "args      run -e API_KEY=hunter2 --token abc https://u:pw@h.example/x?k=v --header X-Key: ••• --static-oauth-client-info ••• img",
                "env       GITHUB_TOKEN = ••• (set)",
                "other     apiKey",
            ],
            ControlMasking.CodeLines(docker, ControlMasking.Exposure.Dialog));
        var oauth = OAuthClient("https://h.example/mcp?tenant=acme", "read");
        Assert.Equal(
            [
                "type     remote, through mcp-remote",
                "url      https://h.example/mcp?tenant=acme",
                "oauth    client cid, secret ••• (set)",
                "scopes   read",
            ],
            ControlMasking.CodeLines(oauth, ControlMasking.Exposure.Dialog));
        Assert.Equal(["command   node", "args      --static-oauth-client-info=•••"],
            ControlMasking.CodeLines(Local("node", ["""--static-oauth-client-info={"client_id":"a","client_secret":"csec"}"""]), ControlMasking.Exposure.Dialog));
        Assert.Equal(
            [
                "type      remote",
                "url       https://mcp.notion.com/mcp?x=1",
                "headers   Authorization = ••• (set)",
                "          X-Empty = needs your value",
            ],
            ControlMasking.CodeLines(Notion, ControlMasking.Exposure.Dialog));
        var shown = string.Join("\n", new[] { docker, oauth, Notion }.SelectMany(config =>
            ControlMasking.CodeLines(config, ControlMasking.Exposure.Dialog)
                .Concat(ControlMasking.ChangeLines(JsonValue.Object(), config, ControlMasking.Exposure.Dialog))));
        foreach (var secret in new[] { Token, "hval", "csec", "topsecret", "t0k" })
        {
            Assert.DoesNotContain(secret, shown);
        }
        Assert.Equal(["apiKey", "  + ••• (set)"],
            ControlMasking.ChangeLines(Local("node", []), Adding("apiKey", S("topsecret"), Local("node", [])), ControlMasking.Exposure.Dialog));
    }

    [Fact]
    public void ANonStringEnvironmentOrHeaderValueReadsAsSet()
    {
        var node = JsonValue.Object(("command", S("node")), ("env", JsonValue.Object(("PORT", JsonValue.Int(8080)), ("TOKEN", S("hunter2")))));
        Assert.Equal(["command   node", "env       PORT = ••• (set)", "          TOKEN = ••• (set)"], ControlMasking.CodeLines(node));
        Assert.Equal(
            JsonValue.Object(("command", S("node")), ("env", JsonValue.Object(("PORT", S("set")), ("TOKEN", S("set"))))),
            ControlMasking.MaskedConfig(node));
        var native = JsonValue.Object(("url", S("https://h.example/mcp")), ("headers", JsonValue.Object(("X-Retries", JsonValue.Int(3)))));
        Assert.Equal(["type      remote", "url       https://h.example/mcp", "headers   X-Retries = ••• (set)"], ControlMasking.CodeLines(native));
        Assert.Equal("headers   X-Retries = 3", ControlMasking.CodeLines(native, ControlMasking.Exposure.Revealed)[^1]);
        var bridged = Remote(["--header", "Authorization:${AUTH_HEADER}"],
            new() { ["AUTH_HEADER"] = S("Bearer s3cr3t"), ["RETRIES"] = JsonValue.Int(3) });
        Assert.Equal(
            [
                "type      remote, through mcp-remote",
                "url       https://h.example/mcp",
                "headers   Authorization = ••• (set)",
                "env       RETRIES = ••• (set)",
            ],
            ControlMasking.CodeLines(bridged));
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
    public void ChangeLinesForALocalConnector()
    {
        var after = JsonValue.Object(
            ("command", S("npx")),
            ("args", JsonValue.Array([S("-y"), S("@modelcontextprotocol/server-github"), S("--read-only")])),
            ("env", JsonValue.Object(("GITHUB_TOKEN", S(Token + "x")), ("EMPTY", S("x")), ("LATER", S("${CC_NEEDS:LATER}")))));
        Assert.Equal(
            [
                "args",
                "  − -y @modelcontextprotocol/server-github",
                "  + -y @modelcontextprotocol/server-github --read-only",
                "env",
                "  − EMPTY = needs your value",
                "  − GITHUB_TOKEN = ••• (set)",
                "  + EMPTY = ••• (set)",
                "  + GITHUB_TOKEN = ••• (set)",
            ],
            ControlMasking.ChangeLines(Github, after));
    }

    /// <summary>Every difference between two configs shows as at least one change, whatever the view: a
    /// confirmation that showed none would let a change through unseen.</summary>
    [Fact]
    public void EveryDifferenceShowsAsAChange()
    {
        const string info = """{"client_id":"a","client_secret":"s"}""";
        var url = JsonValue.Object(("url", S("https://h.example/mcp")));
        (string Name, JsonValue Before, JsonValue After)[] edits =
        [
            ("another key", Adding("cwd", S("/a"), Local("node", ["s.js"])), Adding("cwd", S("/b"), Local("node", ["s.js"]))),
            ("type", Adding("type", S("sse"), Notion), Adding("type", S("http"), Notion)),
            ("unparseable client info", Remote(["--static-oauth-client-info", "not json one"]),
                Remote(["--static-oauth-client-info", "not json two"])),
            ("client metadata", Remote(["--static-oauth-client-info", info, "--static-oauth-client-metadata", """{"redirect_uri":"https://a.example/cb","scope":"read"}"""]),
                Remote(["--static-oauth-client-info", info, "--static-oauth-client-metadata", """{"redirect_uri":"https://evil.example/cb","scope":"read"}"""])),
            ("an env value", Adding("env", JsonValue.Object(("A", S("one"))), Local("node", [])),
                Adding("env", JsonValue.Object(("A", S("two"))), Local("node", []))),
            ("a header value", Adding("headers", JsonValue.Object(("Authorization", S("Bearer one"))), url),
                Adding("headers", JsonValue.Object(("Authorization", S("Bearer two"))), url)),
            ("a bridged env value", Remote([], new() { ["DEBUG"] = S("1") }), Remote([], new() { ["DEBUG"] = S("2") })),
            ("a bearer token", Splunk("https://h.example/mcp", "one"), Splunk("https://h.example/mcp", "two")),
            ("arguments split differently", Local("node", ["--name", "x y"]), Local("node", ["--name", "x", "y"])),
            ("-y dropped", Remote([]), Remote([], bridge: ["mcp-remote"])),
            ("a header beside an OAuth client", Remote(["--static-oauth-client-info", info, "--header", "X:${AUTH_HEADER}"], new() { ["AUTH_HEADER"] = S("v1") }),
                Remote(["--static-oauth-client-info", info, "--header", "X:${AUTH_HEADER}"], new() { ["AUTH_HEADER"] = S("v2") })),
            ("an empty env", Local("node", []), Adding("env", JsonValue.Object(), Local("node", []))),
            ("a non-string env value", Remote([], new() { ["RETRIES"] = JsonValue.Int(3) }), Remote([], new() { ["RETRIES"] = JsonValue.Int(4) })),
            ("a url key beside the bridge", Remote([]), Adding("url", S("https://h.example/mcp"), Remote([]))),
        ];
        foreach (var (name, before, after) in edits)
        {
            foreach (var exposure in new[] { ControlMasking.Exposure.Masked, ControlMasking.Exposure.Dialog, ControlMasking.Exposure.Revealed })
            {
                Assert.True(ControlMasking.ChangeLines(before, after, exposure).Count > 0, $"{name}, {exposure}");
                Assert.True(ControlMasking.ChangeLines(before, before, exposure).Count == 0, $"{name}, {exposure}");
            }
        }
        Assert.Equal(["cwd", "  − /a", "  + /b"], ControlMasking.ChangeLines(edits[0].Before, edits[0].After));
        Assert.Equal(["type", "  − sse", "  + http"], ControlMasking.ChangeLines(edits[1].Before, edits[1].After));
        Assert.Equal(["args", "  − --static-oauth-client-info •••", "  + --static-oauth-client-info •••"],
            ControlMasking.ChangeLines(edits[2].Before, edits[2].After));
        Assert.Equal(["args", "  − --static-oauth-client-metadata •••", "  + --static-oauth-client-metadata •••"],
            ControlMasking.ChangeLines(edits[3].Before, edits[3].After));
        Assert.Equal(
            [
                "args",
                """  − --static-oauth-client-metadata {"redirect_uri":"https://a.example/cb","scope":"read"}""",
                """  + --static-oauth-client-metadata {"redirect_uri":"https://evil.example/cb","scope":"read"}""",
            ],
            ControlMasking.ChangeLines(edits[3].Before, edits[3].After, ControlMasking.Exposure.Dialog));
        foreach (var exposure in new[] { ControlMasking.Exposure.Masked, ControlMasking.Exposure.Dialog })
        {
            Assert.Equal(["env", "  − A = ••• (set)", "  + A = ••• (set)"], ControlMasking.ChangeLines(edits[4].Before, edits[4].After, exposure));
            Assert.Equal(["apiKey", "  − ••• (set)", "  + ••• (set)"],
                ControlMasking.ChangeLines(Adding("apiKey", S("one"), Local("node", [])), Adding("apiKey", S("two"), Local("node", [])), exposure));
        }
        Assert.Equal(["env", "  − A = one", "  + A = two"], ControlMasking.ChangeLines(edits[4].Before, edits[4].After, ControlMasking.Exposure.Revealed));
    }

    /// <summary>A key no field models shows its value as one line of JSON, written the same on both platforms.</summary>
    [Fact]
    public void AnUnmodelledKeyShowsItsValueAsJson()
    {
        var metadata = JsonValue.Object(("n", JsonValue.Int(1)), ("name", S("café")), ("redirect", S("https://a.example/cb")));
        foreach (var exposure in new[] { ControlMasking.Exposure.Masked, ControlMasking.Exposure.Dialog, ControlMasking.Exposure.Revealed })
        {
            Assert.Equal(["metadata", """  + {"n":1,"name":"café","redirect":"https://a.example/cb"}"""],
                ControlMasking.ChangeLines(Notion, Adding("metadata", metadata, Notion), exposure));
        }
        var nested = JsonValue.Object(("a", JsonValue.Array([JsonValue.Double(1.5), JsonValue.Bool(true), JsonValue.Null])), ("q", S("""say "hi" \ now""")));
        Assert.Equal(["nested", """  + {"a":[1.5,true,null],"q":"say \"hi\" \\ now"}"""],
            ControlMasking.ChangeLines(Notion, Adding("nested", nested, Notion)));
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
        AssertHidden(["987654321"], JsonValue.Object(("command", S("node")), ("args", JsonValue.Array([S("--token"), JsonValue.Int(987654321)]))));
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

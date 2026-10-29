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

    /// <summary>A config with a command and nothing else: an empty <c>args</c> array is a key of its own to the lines.</summary>
    private static JsonValue Bare(string command) => JsonValue.Object(("command", S(command)));

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
    /// only an environment value under a secret's name, header values, the OAuth client secret and a
    /// key named for a secret stay masked.</summary>
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
                """args      run -e API_KEY=hunter2 --token abc https://u:pw@h.example/x?k=v --header 'X-Key: •••' --static-oauth-client-info {"client_id":"a","client_secret":"set"} img""",
                "env       GITHUB_TOKEN = ••• (set)",
                "apiKey    ••• (set)",
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
        Assert.Equal(["command   node", """args      --static-oauth-client-info={"client_id":"a","client_secret":"set"}"""],
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

    /// <summary>What would run includes every key, not only those the lines model: a key no field shows
    /// gets a line of its own, and so does an argument mcp-remote's decoded form does not carry.</summary>
    [Fact]
    public void CodeLinesShowEveryKeyAndArgumentTheFieldsDoNot()
    {
        var cwd = Adding("cwd", S("/srv/app"), Local("node", ["s.js"]));
        Assert.Equal(["command   node", "args      s.js", "cwd       /srv/app"], ControlMasking.CodeLines(cwd, ControlMasking.Exposure.Dialog));
        var sse = Adding("type", S("sse"), Notion);
        Assert.Equal(
            [
                "type      remote",
                "          sse",
                "url       https://mcp.notion.com/mcp?x=1",
                "headers   Authorization = ••• (set)",
                "          X-Empty = needs your value",
            ],
            ControlMasking.CodeLines(sse, ControlMasking.Exposure.Dialog));
        var bridged = Adding("cwd", S("/tmp"),
            Remote(["--static-oauth-client-metadata", """{"redirect_uri":"https://evil.example/cb"}"""]));
        Assert.Equal(
            [
                "type   remote, through mcp-remote",
                "url    https://h.example/mcp",
                """args   --static-oauth-client-metadata {"redirect_uri":"https://evil.example/cb"}""",
                "cwd    /tmp",
            ],
            ControlMasking.CodeLines(bridged, ControlMasking.Exposure.Dialog));
        var secret = JsonValue.Object(("command", S("node")), ("apiKey", S("topsecret")),
            ("auth", JsonValue.Object(("user", S("bob")), ("pass", S("hunter2")))));
        Assert.Equal(["command   node", "apiKey    ••• (set)", """auth      {"pass":"set","user":"set"}"""],
            ControlMasking.CodeLines(secret, ControlMasking.Exposure.Dialog));
        foreach (var exposure in new[] { ControlMasking.Exposure.Masked, ControlMasking.Exposure.Dialog, ControlMasking.Exposure.Revealed })
        {
            var lines = new[] { cwd, sse, bridged, secret }.Select(config => string.Join("\n", ControlMasking.CodeLines(config, exposure))).ToList();
            Assert.True(lines[0].Contains("cwd       /srv/app"), $"{exposure}");
            Assert.True(lines[1].Contains("          sse"), $"{exposure}");
            Assert.True(lines[2].Contains("args   --static-oauth-client-metadata ") && lines[2].Contains("cwd    /tmp"), $"{exposure}");
            Assert.True(lines[3].Contains("apiKey    "), $"{exposure}");
            Assert.True((lines[3].Contains("topsecret") || lines[3].Contains("hunter2")) == (exposure == ControlMasking.Exposure.Revealed), $"{exposure}");
        }
    }

    /// <summary>In the dialog a single value reads one way only, as an argument does: the command, a URL, a
    /// key's text value and a variable's name are quoted, with \ and ' escaped, when they hold a space, a
    /// quote or anything shown as an escape — so a newline and the two characters \n read apart, and a run
    /// of spaces cannot pass for the start of another row. What Claude and the command line read is
    /// unchanged.</summary>
    [Fact]
    public void TheDialogShowsASingleValueOneWayOnly()
    {
        var literal = Bare("""run\n""");
        var newline = Bare("run\n");
        Assert.Equal(["""command   run\n"""], ControlMasking.CodeLines(literal, ControlMasking.Exposure.Dialog));
        Assert.Equal(["""command   'run\n'"""], ControlMasking.CodeLines(newline, ControlMasking.Exposure.Dialog));
        var spaced = Adding("cwd", JsonValue.String("/srv        env   X = y"), Bare("node"));
        Assert.Equal(["command   node", "cwd       '/srv        env   X = y'"], ControlMasking.CodeLines(spaced, ControlMasking.Exposure.Dialog));
        var named = Adding("env", JsonValue.Object(("A B = c", JsonValue.String("v"))), Bare("node"));
        Assert.Equal(["command   node", "env       'A B = c' = v"], ControlMasking.CodeLines(named, ControlMasking.Exposure.Dialog));
        var address = JsonValue.Object(("url", JsonValue.String("https://h.example/a b")));
        Assert.Equal(["type   remote", "url    'https://h.example/a b'"], ControlMasking.CodeLines(address, ControlMasking.Exposure.Dialog));
        var keyed = Adding("a b", JsonValue.String("v"), Bare("node"));
        Assert.Equal(["command   node", "'a b'     v"], ControlMasking.CodeLines(keyed, ControlMasking.Exposure.Dialog));
        // The masked lines are unchanged.
        Assert.Equal(["""command   run\n"""], ControlMasking.CodeLines(newline));
        Assert.Equal(["""command   run\n"""], ControlMasking.CodeLines(literal));
    }

    /// <summary>The dialog masks the OAuth client secret, not the client: the client info shows with only
    /// its secret, and any value under a key named for a secret, masked. Text that is not a JSON
    /// object stays masked whole, and tool output masks it whole either way.</summary>
    [Fact]
    public void TheDialogShowsTheOAuthClientInfoButItsSecret()
    {
        const string info = """{"client_id":"cid9","client_secret":"csec9","redirect_uris":["https://a.example/cb"]}""";
        Assert.Equal(
            [
                "command   node",
                """args      --static-oauth-client-info {"client_id":"cid9","client_secret":"set","redirect_uris":["https://a.example/cb"]}""",
            ],
            ControlMasking.CodeLines(Local("node", ["--static-oauth-client-info", info]), ControlMasking.Exposure.Dialog));
        Assert.Equal("args      --static-oauth-client-info •••",
            ControlMasking.CodeLines(Local("node", ["--static-oauth-client-info", info]), ControlMasking.Exposure.Masked)[^1]);
        foreach (var text in new[] { "not-json-csec8", "\"csec7\"", "[\"csec6\"]" })
        {
            Assert.Equal("args      --static-oauth-client-info •••",
                ControlMasking.CodeLines(Local("node", ["--static-oauth-client-info", text]), ControlMasking.Exposure.Dialog)[^1]);
            Assert.Equal("args      --static-oauth-client-info=•••",
                ControlMasking.CodeLines(Local("node", ["--static-oauth-client-info=" + text]), ControlMasking.Exposure.Dialog)[^1]);
        }
        var bridged = Remote(["--static-oauth-client-info", """{"client_id":"a","client_secret":"csec5","extra":1}"""]);
        Assert.Equal(
            [
                "type    remote, through mcp-remote",
                "url     https://h.example/mcp",
                "oauth   client a, secret ••• (set)",
                """args    --static-oauth-client-info {"client_id":"a","client_secret":"set","extra":1}""",
            ],
            ControlMasking.CodeLines(bridged, ControlMasking.Exposure.Dialog));
    }

    /// <summary>What the lines show reads back as what would run: two argument lists read differently, an
    /// empty argument shows, and a newline or a bidi control in a value cannot forge or hide a row.</summary>
    [Fact]
    public void TheLinesCannotBeMisreadOrForged()
    {
        var spaced = Local("node", ["--name", "x y"]);
        var split = Local("node", ["--name", "x", "y"]);
        var forged = Local("node", ["s.js", "x\nenv       SAFE = ••• (set)"]);
        var keyed = JsonValue.Object(("command", S("node")), ("x\nenv", S("v\nSAFE = ••• (set)")),
            ("url", S("https://h.example/\u202Egpj.exe")));
        foreach (var exposure in new[] { ControlMasking.Exposure.Masked, ControlMasking.Exposure.Dialog, ControlMasking.Exposure.Revealed })
        {
            Assert.Equal("args      --name 'x y'", ControlMasking.CodeLines(spaced, exposure)[^1]);
            Assert.Equal("args      --name x y", ControlMasking.CodeLines(split, exposure)[^1]);
            Assert.Equal(["args", "  − --name 'x y'", "  + --name x y"], ControlMasking.ChangeLines(spaced, split, exposure));
            Assert.Equal("args      a '' b", ControlMasking.CodeLines(Local("node", ["a", "", "b"]), exposure)[^1]);
            Assert.Equal(["command   node", """args      s.js 'x\nenv       SAFE = ••• (set)'"""], ControlMasking.CodeLines(forged, exposure));
            Assert.Equal(["args", """  + s.js 'x\nenv       SAFE = ••• (set)'"""],
                ControlMasking.ChangeLines(JsonValue.Object(("command", S("node"))), forged, exposure));
            foreach (var line in ControlMasking.CodeLines(keyed, exposure).Concat(ControlMasking.ChangeLines(JsonValue.Object(), keyed, exposure)))
            {
                Assert.False(line.Contains('\n') || line.Contains('\u202E'), $"{exposure}: {line}");
            }
        }
        Assert.Equal(["command    node", """url        'https://h.example/\u{202E}gpj.exe'""", """'x\nenv'   'v\nSAFE = ••• (set)'"""],
            ControlMasking.CodeLines(keyed, ControlMasking.Exposure.Dialog));
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

    /// <summary>The dialog shows an environment value as written, quoted as an argument is, since a variable
    /// can change what runs (<c>NODE_OPTIONS</c>, <c>PATH</c>, <c>LD_PRELOAD</c>): every one but a variable whose
    /// name reads as a secret's. A header's value stays masked whatever its name, and tool output and
    /// <c>show --reveal</c> are as they were.</summary>
    [Fact]
    public void TheDialogShowsAnEnvironmentValueUnlessItsNameIsASecrets()
    {
        var node = JsonValue.Object(("command", S("node")), ("env", JsonValue.Object(
            ("NODE_OPTIONS", S("--require ./hook.js")), ("PATH", S("/opt/x/bin")), ("PORT", JsonValue.Int(8080)),
            ("EMPTY", S("")), ("API_KEY", S("hunter2")), ("DB_PASSWORD", S("")))));
        Assert.Equal(
            [
                "command   node",
                "env       API_KEY = ••• (set)",
                "          DB_PASSWORD = needs your value",
                "          EMPTY = ''",
                "          NODE_OPTIONS = '--require ./hook.js'",
                "          PATH = /opt/x/bin",
                "          PORT = 8080",
            ],
            ControlMasking.CodeLines(node, ControlMasking.Exposure.Dialog));
        // Tool output masks every one.
        Assert.Equal(
            [
                "command   node",
                "env       API_KEY = ••• (set)",
                "          DB_PASSWORD = needs your value",
                "          EMPTY = needs your value",
                "          NODE_OPTIONS = ••• (set)",
                "          PATH = ••• (set)",
                "          PORT = ••• (set)",
            ],
            ControlMasking.CodeLines(node));
        Assert.Equal("          NODE_OPTIONS = --require ./hook.js", ControlMasking.CodeLines(node, ControlMasking.Exposure.Revealed)[4]);
        var bridged = Remote(["--header", "Authorization:${AUTH_HEADER}"],
            new() { ["AUTH_HEADER"] = S("Bearer s3cr3t"), ["NODE_OPTIONS"] = S("--require ./hook.js") });
        Assert.Equal(
            [
                "type      remote, through mcp-remote",
                "url       https://h.example/mcp",
                "headers   Authorization = ••• (set)",
                "env       NODE_OPTIONS = '--require ./hook.js'",
            ],
            ControlMasking.CodeLines(bridged, ControlMasking.Exposure.Dialog));
        var native = JsonValue.Object(("url", S("https://h.example/mcp")), ("headers", JsonValue.Object(("X-Region", S("eu")))));
        // A header stays masked.
        Assert.Equal("headers   X-Region = ••• (set)", ControlMasking.CodeLines(native, ControlMasking.Exposure.Dialog)[^1]);
        var before = Adding("env", JsonValue.Object(("NODE_OPTIONS", S("--require ./a.js")), ("API_KEY", S("secret-one"))), Local("node", []));
        var after = Adding("env", JsonValue.Object(("NODE_OPTIONS", S("--require ./b.js")), ("API_KEY", S("secret-two"))), Local("node", []));
        Assert.Equal(
            [
                "env",
                "  − API_KEY = ••• (set)",
                "  − NODE_OPTIONS = '--require ./a.js'",
                "  + API_KEY = ••• (set)",
                "  + NODE_OPTIONS = '--require ./b.js'",
            ],
            ControlMasking.ChangeLines(before, after, ControlMasking.Exposure.Dialog));
        Assert.Equal(["env", "  + API_KEY = ••• (set)", "  + NODE_OPTIONS = '--require ./a.js'"],
            ControlMasking.ChangeLines(Local("node", []), before, ControlMasking.Exposure.Dialog));
        var dialog = string.Join("\n", ControlMasking.CodeLines(node, ControlMasking.Exposure.Dialog)
            .Concat(ControlMasking.ChangeLines(before, after, ControlMasking.Exposure.Dialog)));
        foreach (var secret in new[] { "hunter2", "secret-one", "secret-two" })
        {
            Assert.DoesNotContain(secret, dialog);
        }
        var masked = string.Join("\n", ControlMasking.CodeLines(node).Concat(ControlMasking.ChangeLines(before, after)));
        foreach (var value in new[] { "hook.js", "/opt/x/bin", "8080", "a.js", "b.js", "secret-one", "secret-two" })
        {
            Assert.DoesNotContain(value, masked);
        }
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
            ("a secret's env value", Adding("env", JsonValue.Object(("API_KEY", S("one"))), Local("node", [])),
                Adding("env", JsonValue.Object(("API_KEY", S("two"))), Local("node", []))),
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
            Assert.Equal(["env", "  − API_KEY = ••• (set)", "  + API_KEY = ••• (set)"], ControlMasking.ChangeLines(edits[4].Before, edits[4].After, exposure));
            Assert.Equal(["apiKey", "  − ••• (set)", "  + ••• (set)"],
                ControlMasking.ChangeLines(Adding("apiKey", S("one"), Local("node", [])), Adding("apiKey", S("two"), Local("node", [])), exposure));
        }
        Assert.Equal(["env", "  − API_KEY = one", "  + API_KEY = two"], ControlMasking.ChangeLines(edits[4].Before, edits[4].After, ControlMasking.Exposure.Revealed));
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

    /// <summary><c>env</c> and <c>headers</c> hold secrets wherever they sit, not only at the top of a config.</summary>
    [Fact]
    public void EnvironmentAndHeadersUnderAnotherKeyAreMasked()
    {
        var nested = JsonValue.Object(
            ("command", S("node")), ("args", JsonValue.Array([S("s.js")])),
            ("options", JsonValue.Object(("env", JsonValue.Object(("DB", S("hunter2a")))), ("headers", JsonValue.Object(("X-Custom", S("hunter2b")))))));
        var transport = JsonValue.Object(
            ("url", S("https://h.example/mcp")),
            ("transport", JsonValue.Object(("headers", JsonValue.Object(("X-Custom", S("hunter2c")))))));
        AssertHidden(["hunter2a", "hunter2b"], nested);
        AssertHidden(["hunter2c"], transport);
        Assert.Equal(["command   node", "args      s.js", """options   {"env":{"DB":"set"},"headers":{"X-Custom":"set"}}"""],
            ControlMasking.CodeLines(nested, ControlMasking.Exposure.Dialog));
        Assert.Equal(["type        remote", "url         https://h.example/mcp", """transport   {"headers":{"X-Custom":"set"}}"""],
            ControlMasking.CodeLines(transport, ControlMasking.Exposure.Dialog));
        var dialog = string.Join("\n", ControlMasking.ChangeLines(JsonValue.Object(), nested, ControlMasking.Exposure.Dialog)
            .Concat(ControlMasking.ChangeLines(JsonValue.Object(), transport, ControlMasking.Exposure.Dialog)));
        foreach (var secret in new[] { "hunter2a", "hunter2b", "hunter2c" })
        {
            Assert.DoesNotContain(secret, dialog);
        }
        Assert.True(dialog.Contains("\"DB\":\"set\"") && dialog.Contains("\"X-Custom\":\"set\""));
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

    /// <summary>The heuristics read a name unit by unit on both platforms, so a combining mark after it
    /// does not unmask its value on the Mac only.</summary>
    [Fact]
    public void ACombiningMarkAfterASecretNameKeepsItsValueMasked()
    {
        Assert.Equal("--api-key\u0301=•••", ControlMasking.Argument("--api-key\u0301=hunter2y"));
        Assert.Equal(["--api-key\u0301", "•••"], ControlMasking.Arguments(["--api-key\u0301", "hunter2x"]));
        Assert.Equal("API_TOKEN\u0301=•••", ControlMasking.Argument("API_TOKEN\u0301=hunter2z"));
        AssertHidden(["hunter2x", "hunter2y", "hunter2z"],
            Local("node", ["--api-key\u0301=hunter2y", "--api-key\u0301", "hunter2x", "API_TOKEN\u0301=hunter2z"]));
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

    /// <summary>A shell line packed into one argument (<c>bash -c "…"</c>, <c>cmd /c …</c>), or a whole command line
    /// written as the command, is masked word by word as a shell passes its words, so a flag named
    /// for a secret among them still hides the word after it and a URL among them is not the whole.</summary>
    [Fact]
    public void AShellLineInOneArgumentIsMaskedWordByWord()
    {
        Assert.Equal(["-c", "npx -y s --token •••"], ControlMasking.Arguments(["-c", "npx -y s --token hunter2abc"]));
        const string bash = "npx -y mcp-remote https://h/sse --header 'Authorization: Bearer eyJhbGc.eyJzdWI.sig-x_y'";
        Assert.Equal(["-c", "npx -y mcp-remote https://h/sse --header 'Authorization: •••'"],
            ControlMasking.Arguments(["-c", bash]));
        Assert.Equal("API_KEY=••• npx server https://h.example/mcp",
            ControlMasking.Argument("API_KEY=sk_live_x npx server https://h.example/mcp"));
        Assert.Equal(["command   npx -y s --token •••"], ControlMasking.CodeLines(Bare("npx -y s --token hunter2abc")));
        AssertHidden(["hunter2abc"], Local("bash", ["-c", "npx -y s --token hunter2abc"]));
        AssertHidden(["eyJhbGc", "sig-x_y"], Local("bash", ["-c", bash]));
        AssertHidden(["sk_live_x"], Local("sh", ["-c", "API_KEY=sk_live_x npx server https://h.example/mcp"]));
        AssertHidden(["hunter2abc"], Local("cmd", ["/c", "npx -y s --api-key hunter2abc"]));
        AssertHidden(["hunter2abc"], Bare("npx -y s --token hunter2abc"));
    }

    /// <summary>A value with spaces and no secret reads as written, quotes and all.</summary>
    [Fact]
    public void AShellLineWithNoSecretReadsAsWritten()
    {
        foreach (var text in new[] { "hello world", "echo \"a b\" 'c d'", "C:\\Program Files\\nodejs\\node.exe", "npx -y server --port 8080" })
        {
            Assert.Equal(text, ControlMasking.Argument(text));
        }
    }

    /// <summary>JSON passed as an argument — Smithery's <c>run &lt;server&gt; --config '{"apiKey":"…"}'</c> — is masked
    /// leaf by leaf, by key, as a config is; JSON with nothing to mask reads as written.</summary>
    [Fact]
    public void AJSONArgumentIsMaskedByKey()
    {
        string[] smithery = ["-y", "@smithery/cli", "run", "@x/server", "--config", """{"apiKey":"sk_abc123","region":"us"}"""];
        Assert.Equal(["-y", "@smithery/cli", "run", "@x/server", "--config", """{"apiKey":"set","region":"us"}"""],
            ControlMasking.Arguments(smithery));
        Assert.Equal("""[{"token":"set"},"plain"]""", ControlMasking.Argument("""[{"token": "hunter2x"}, "plain"]"""));
        Assert.Equal("""{"region": "us", "port": 8080}""", ControlMasking.Argument("""{"region": "us", "port": 8080}"""));
        AssertHidden(["sk_abc123"], Local("npx", smithery));
        AssertHidden(["sk_abc123"], Bare("""npx -y @smithery/cli run @x/server --config '{"apiKey": "sk_abc123"}'"""));
    }

    /// <summary>JSON nested deeper than either platform reads is masked whole, alike on both: what it holds
    /// cannot be read leaf by leaf.</summary>
    [Fact]
    public void JSONNestedDeeperThan64IsMaskedWholeAlike()
    {
        var deep = string.Concat(Enumerable.Repeat("""{"a":""", 69)) + """{"apiKey":"sk_abc123"}""" + new string('}', 69);
        Assert.Equal(ControlMasking.Mask, ControlMasking.Argument(deep));
        Assert.Equal(["--static-oauth-client-info", ControlMasking.Mask],
            ControlMasking.Arguments(["--static-oauth-client-info", deep], ControlMasking.Exposure.Dialog));
        AssertHidden(["sk_abc123"], Local("npx", ["--config", deep]));
    }
}

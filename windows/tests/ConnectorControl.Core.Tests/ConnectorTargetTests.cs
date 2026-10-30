namespace ConnectorControl.Core.Tests;

/// <summary>
/// Mirror: Tests/ConnectorControlCoreTests/ConnectorTargetTests.swift.
/// What a connector's row, or an answer to Claude, says it runs: the allowlist that never shows a
/// secret.
/// </summary>
public class ConnectorTargetTests
{
    /// <summary>A local connector running <paramref name="command"/> with <paramref name="args"/>.</summary>
    private static McpEntry LocalConnector(string command, params string[] args) =>
        new(JsonValue.Object(
            ("command", JsonValue.String(command)),
            ("args", JsonValue.Array(args.Select(JsonValue.String)))));

    private static JsonValue Remote(string url) => RemotePattern.Make(url, RemoteLaunchStyle.Npx);

    /// <summary>The target a local connector shows, which must not carry <paramref name="secret"/> whatever else it says.</summary>
    private static void AssertTarget(McpEntry entry, string expected, string secret)
    {
        var target = ConnectorTarget.TargetOf(entry.Config, "/Users/x");
        Assert.DoesNotContain(secret, target);
        Assert.Equal(expected, target);
    }

    [Fact]
    public void TargetShowsARemoteConnectorsHostOnly()
    {
        Assert.Equal("api.githubcopilot.com",
            ConnectorTarget.TargetOf(Remote("https://u:p@api.githubcopilot.com/mcp?k=v"), "/Users/x"));
        Assert.Equal("localhost:8080", ConnectorTarget.TargetOf(Remote("http://localhost:8080/mcp"), "/Users/x"));
    }

    [Fact]
    public void TargetShowsOnlyTheHostOfARemoteConnectorWithAHeader() =>
        AssertTarget(LocalConnector("npx", "-y", "mcp-remote", "https://h.example/mcp", "--header", "Authorization: Bearer abc"),
            "h.example", "abc");

    /// <summary>
    /// A connector Claude reaches by URL, with no command at all, shows the URL's scheme and host
    /// through the same strict reading as a URL argument; one whose host is in doubt, or that has no
    /// URL to read, shows only that it is remote.
    /// </summary>
    [Fact]
    public void TargetShowsAUrlOnlyConnectorsSchemeAndHost()
    {
        (JsonValue Config, string Expected)[] cases =
        [
            (JsonValue.Object(("url", JsonValue.String("https://mcp.example.com/mcp"))), "https://mcp.example.com"),
            (JsonValue.Object(("type", JsonValue.String("http")), ("url", JsonValue.String("https://u:SECRET@h.example:8443/mcp"))), "https://h.example:8443"),
            (JsonValue.Object(("url", JsonValue.String("https://u:p@SECRET@h.example/mcp"))), "https://h.example"),
            (JsonValue.Object(("url", JsonValue.String("https://u:p/SECRET@h.example/mcp"))), ConnectorTarget.RemoteType),
            (JsonValue.Object(("url", JsonValue.String("https://u:p?SECRET@h.example/mcp"))), ConnectorTarget.RemoteType),
            (JsonValue.Object(("url", JsonValue.String("https://u:p#SECRET@h.example/mcp"))), ConnectorTarget.RemoteType),
            (JsonValue.Object(("url", JsonValue.String("https://h.example/mcp?api_key=SECRET"))), "https://h.example"),
            (JsonValue.Object(("type", JsonValue.String("sse")), ("url", JsonValue.String("https://h.example/sse")),
                ("headers", JsonValue.Object(("Authorization", JsonValue.String("Bearer SECRET"))))), "https://h.example"),
            (JsonValue.Object(("type", JsonValue.String("http")), ("url", JsonValue.String("h.example/SECRET"))), ConnectorTarget.RemoteType),
            (JsonValue.Object(("url", JsonValue.Int(5))), ConnectorTarget.RemoteType),
            (JsonValue.Object(("type", JsonValue.String("http"))), ConnectorTarget.RemoteType),
        ];
        foreach (var (config, expected) in cases)
        {
            var target = ConnectorTarget.TargetOf(config, "/Users/x");
            Assert.Equal(expected, target);
            Assert.DoesNotContain("SECRET", target);
        }
    }

    [Fact]
    public void TargetShortensAScopedPackageAndTheHomeFolder()
    {
        Assert.Equal("npx …/server-filesystem ~/Documents",
            ConnectorTarget.TargetOf(LocalConnector("npx", "-y", "@modelcontextprotocol/server-filesystem", "/Users/x/Documents").Config, "/Users/x"));
    }

    /// <summary>
    /// A connector authored on Windows abbreviates its home folder the same way, whichever
    /// separator follows it and however its letters are cased.
    /// </summary>
    [Fact]
    public void TargetShortensAWindowsHomeFolder()
    {
        Assert.Equal(@"node ~\srv\index.js …/pkg@1.2 ~\a.js",
            ConnectorTarget.TargetOf(LocalConnector("node", @"C:\Users\x\srv\index.js", "@scope/pkg@1.2", @"c:\users\X\a.js").Config,
                @"C:\Users\x"));
    }

    [Fact]
    public void TargetShowsAUrlArgumentsSchemeAndHostOnly() =>
        AssertTarget(LocalConnector("tool", "https://me:pw@h.example:8443/x?token=t#frag"), "tool https://h.example:8443", "pw");

    [Fact]
    public void TargetLeavesOutASlackWebhooksPath() =>
        AssertTarget(LocalConnector("tool", "https://hooks.slack.com/services/T000/B000/XXXXsecret"), "tool https://hooks.slack.com", "XXXXsecret");

    [Fact]
    public void TargetLeavesOutFlagsAndTheirValues() =>
        AssertTarget(LocalConnector("/usr/local/bin/tool", "--api-key", "abc", "--port", "80", "--token=abc"), "tool", "abc");

    [Fact]
    public void TargetLeavesOutAHeaderFlag() =>
        AssertTarget(LocalConnector("tool", "-H", "X-Api-Key: abc", "https://h.example/x"), "tool https://h.example", "abc");

    [Fact]
    public void TargetLeavesOutAnEnvironmentAssignment() =>
        AssertTarget(LocalConnector("docker", "run", "-i", "--rm", "-e", "GITHUB_TOKEN=abc", "ghcr.io/github/github-mcp-server"),
            "docker ghcr.io/github/github-mcp-server", "abc");

    [Fact]
    public void TargetLeavesOutAShellString()
    {
        AssertTarget(LocalConnector("sh", "-c", "TOKEN=abc node srv.js"), "sh", "abc");
        AssertTarget(LocalConnector("sh", "-c", "curl -H 'Authorization: Bearer abc' https://h.example/x"), "sh", "abc");
    }

    [Fact]
    public void TargetLeavesOutAnAttachedShortFlagValue() => AssertTarget(LocalConnector("mysql-mcp", "-pSECRET"), "mysql-mcp", "SECRET");

    [Fact]
    public void TargetLeavesOutAShortPositionalSecret() => AssertTarget(LocalConnector("tool", "hunter2"), "tool", "hunter2");

    [Fact]
    public void TargetLeavesOutAnUnprefixedKey() => AssertTarget(LocalConnector("tool", "sk_live_abc123"), "tool", "sk_live");

    [Fact]
    public void TargetLeavesOutAConnectionString() => AssertTarget(LocalConnector("tool", "Server=h;Password=x"), "tool", "Password=x");

    [Fact]
    public void TargetLeavesOutInlineJson() => AssertTarget(LocalConnector("tool", "--config", """{"apiKey":"abc"}"""), "tool", "abc");

    [Fact]
    public void TargetLeavesOutTheValueOfASecretNamedFlagWhateverItsCase()
    {
        AssertTarget(LocalConnector("tool", "--token", "x.y"), "tool", "x.y");
        AssertTarget(LocalConnector("tool", "--TOKEN", "abc.def"), "tool", "abc.def");
    }

    /// <summary>
    /// A command line written as one string is split into launcher and arguments and each word
    /// held to the rule, so its last word - often a flag's value - is never shown for the launcher.
    /// </summary>
    [Fact]
    public void TargetSplitsACommandLineWrittenAsOneString()
    {
        AssertTarget(LocalConnector("cmd", "/c", "npx -y @acme/server --api-key hunter2"), "npx …/server", "hunter2");
        AssertTarget(new McpEntry(JsonValue.Object(("command", JsonValue.String("npx -y server --token hunter2")))), "npx", "hunter2");
        AssertTarget(new McpEntry(JsonValue.Object(("command", JsonValue.String("tool --token a.b")))), "tool", "a.b");
        const string quoted = "npx -y mcp-remote https://h.example/mcp --header \"Authorization: Bearer abc\"";
        AssertTarget(LocalConnector("cmd", "/c", quoted), "h.example", "abc");
        Assert.DoesNotContain("Bearer", ConnectorTarget.TargetOf(LocalConnector("cmd", "/c", quoted).Config, "/Users/x"));
    }

    /// <summary>
    /// A quoted argument in a one-string command line - a header value, some JSON - is one
    /// argument, and one holding whitespace fails every shape the column shows; an unterminated
    /// quote runs to the end as part of its word.
    /// </summary>
    [Fact]
    public void TargetLeavesOutAQuotedArgumentInACommandLine()
    {
        AssertTarget(LocalConnector("cmd", "/c", "tool --header \"X-Key: abc.def extra\""), "tool", "abc.def");
        AssertTarget(LocalConnector("cmd", "/c", "npx -y @acme/server --config '{\"k\":\"v.w\"}'"), "npx …/server", "v.w");
        AssertTarget(new McpEntry(JsonValue.Object(("command", JsonValue.String("tool \"abc.def extra")))), "tool", "abc.def");
        AssertTarget(new McpEntry(JsonValue.Object(("command", JsonValue.String("\"hunter.2 x\" srv.js")))), "srv.js", "hunter");
    }

    /// <summary>
    /// A command line is tokenized the way a shell passes it, so a quoted or partly quoted flag
    /// is still a flag, its value still drops, and only the first argument is the launcher.
    /// </summary>
    [Fact]
    public void TargetTokenizesACommandLineLikeAShell()
    {
        AssertTarget(new McpEntry(JsonValue.Object(("command", JsonValue.String(@"""C:\Program Files\Tool\tool.exe"" --api-key hunter.2x")))),
            "tool", "hunter.2x");
        AssertTarget(new McpEntry(JsonValue.Object(("command", JsonValue.String("tool \"--token\" hunter.2x")))), "tool", "hunter.2x");
        AssertTarget(new McpEntry(JsonValue.Object(("command", JsonValue.String("tool --to\"ken\" hunter.2x")))), "tool", "hunter.2x");
        AssertTarget(new McpEntry(JsonValue.Object(("command", JsonValue.String("tool \"a b\" x.y")))), "tool x.y", "a b");
        // An escaped quote does not close the argument, so `c.d` stays inside it.
        AssertTarget(new McpEntry(JsonValue.Object(("command", JsonValue.String(@"tool ""a\""b c.d""")))), "tool", "c.d");
    }

    /// <summary>
    /// A command that is a path is never split, even with a space in it; one whose last component
    /// holds a space had arguments packed into it, and names no launcher.
    /// </summary>
    [Fact]
    public void TargetNamesALauncherUnderProgramFiles()
    {
        AssertTarget(LocalConnector(@"C:\Program Files\nodejs\node.exe", "index.js"), "node index.js", "Program");
        AssertTarget(LocalConnector(@"""C:\Program Files\nodejs\node.exe""", "index.js"), "node index.js", "Program");
        AssertTarget(LocalConnector(@"C:\Program Files\nodejs\npx.cmd", "-y", "mcp-remote", "https://h.example/mcp"), "h.example", "npx");
        AssertTarget(LocalConnector("/opt/bin/tool --password hunter.2x", "srv.js"), "srv.js", "hunter.2x");
        AssertTarget(LocalConnector(@"C:\Program Files (x86)\Tool\tool.exe", "index.js"), "tool index.js", "Program");
        // Plain words packed after a Unix path do not cost it its launcher.
        AssertTarget(LocalConnector("/usr/bin/tool srv"), "tool", "srv");
    }

    /// <summary>
    /// A path command with a flag, a URL or a switch packed into it names no launcher: its last
    /// component could be the tail of an argument.
    /// </summary>
    [Fact]
    public void TargetNamesNoLauncherForAPathCommandWithAPackedArgument()
    {
        AssertTarget(LocalConnector("cmd", "/c", @"C:\tools\notify.exe https://hooks.slack.com/services/T000/B000/XXXXsecret"), "", "XXXXsecret");
        AssertTarget(LocalConnector("/usr/local/bin/mcp --api-key abc/hunter.2x"), "", "hunter.2x");
        AssertTarget(LocalConnector(@"C:\x\tool.exe --token ab\cd.ef"), "", "cd.ef");
    }

    /// <summary>
    /// A flag named for a secret at the end of a command guards the first argument after it,
    /// whether the command is a path or a tokenized line.
    /// </summary>
    [Fact]
    public void TargetLeavesOutTheFirstArgumentAfterASecretNamedFlagInTheCommand()
    {
        AssertTarget(LocalConnector("/opt/bin/tool --password", "hunter.2x"), "", "hunter.2x");
        AssertTarget(LocalConnector("tool --token", "abc.def"), "tool", "abc.def");
    }

    /// <summary>
    /// A quote packed into a path command costs it its launcher, and a quoted or partly quoted
    /// flag at its end is still a flag named for a secret.
    /// </summary>
    [Fact]
    public void TargetReadsAQuotedFlagPackedIntoAPathCommand()
    {
        AssertTarget(LocalConnector(@"/opt/bin/tool ""--password""", "hunter.2x"), "", "hunter.2x");
        AssertTarget(LocalConnector(@"C:\x\tool.exe ""--token"" ab\cd.ef"), "", "cd.ef");
        AssertTarget(LocalConnector(@"/opt/bin/tool --to""ken""", "hunter.2x"), "", "hunter.2x");
    }

    /// <summary>
    /// Each check that keeps a path command plain is load-bearing: a Windows switch and an
    /// assignment packed in both cost it its launcher.
    /// </summary>
    [Fact]
    public void TargetNamesNoLauncherForAPathCommandWithASwitchOrAnAssignment()
    {
        AssertTarget(LocalConnector(@"C:\x\tool.exe /key ab\cd.ef"), "", "cd.ef");
        AssertTarget(LocalConnector("/opt/bin/tool key=ab/cd.ef"), "", "cd.ef");
    }

    /// <summary>
    /// A password holding an unencoded <c>/</c>, <c>?</c> or <c>#</c> ends the authority early; what
    /// is left of the userinfo is refused as a host rather than shown, and so is a scheme that is not one.
    /// </summary>
    [Fact]
    public void TargetRefusesAUrlWhoseUserinfoHoldsADelimiter()
    {
        AssertTarget(LocalConnector("tool", "postgres://admin:hunter2#x@db.local/app"), "tool", "hunter2");
        AssertTarget(LocalConnector("tool", "https://apikey:sk_live_abc/x@api.example.com"), "tool", "sk_live");
        AssertTarget(LocalConnector("tool", "https://sk_live_abc/x@h"), "tool", "sk_live");
        AssertTarget(LocalConnector("tool", "sk-proj-abc123://x"), "tool", "abc123");
        AssertTarget(LocalConnector("tool", "mongodb+srv://u:p@cluster.example.net/db"), "tool mongodb+srv://cluster.example.net", "u:p");
        // The remote decoder accepts this URL, and its host is still checked.
        AssertTarget(LocalConnector("npx", "-y", "mcp-remote", "https://token123/x@h.example/mcp"), ConnectorTarget.RemoteType, "token123");
    }

    /// <summary>
    /// A long random token is left out even with a <c>/</c> in it, which <c>LooksLikeCredential</c>
    /// would not consider; paths and names with a dot, a hyphen or no digits are kept.
    /// </summary>
    [Fact]
    public void TargetLeavesOutARandomTokenEvenWithASlash()
    {
        AssertTarget(LocalConnector("tool", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"), "tool", "wJalr");
        Assert.Equal("tool ~/Documents ghcr.io/github/github-mcp-server ./build/v2Server",
            ConnectorTarget.TargetOf(LocalConnector("tool", "/Users/x/Documents", "ghcr.io/github/github-mcp-server", "./build/v2Server").Config,
                "/Users/x"));
    }

    /// <summary>A Windows switch's value is not a path: a colon anywhere but a drive letter's drops it.</summary>
    [Fact]
    public void TargetLeavesOutAWindowsSwitchesValue()
    {
        AssertTarget(LocalConnector("tool", "/p:Hunter2", "/token:abc", "/x:secret"), "tool", "secret");
        AssertTarget(LocalConnector("tool", @"C:\Users\x\Docs"), @"tool C:\Users\x\Docs", "secret");
    }

    /// <summary>A secret shaped like a package still vanishes when a flag named for a secret precedes it.</summary>
    [Fact]
    public void TargetLeavesOutWhateverFollowsASecretNamedFlag() =>
        AssertTarget(LocalConnector("tool", "--password", "s3cr3t-pass", "--pass", "./x.key", "index.js"), "tool index.js", "s3cr3t");

    [Fact]
    public void TargetLeavesOutAPathCarryingAnAssignment() => AssertTarget(LocalConnector("tool", "/usr/bin/env TOKEN=abc"), "tool", "abc");

    [Fact]
    public void TargetLeavesOutACredentialShapedArtefact() =>
        AssertTarget(LocalConnector("tool", "sk-abc.def", "ghp_0123456789abcdef0123456789abcdef.js"), "tool", "abc");

    [Fact]
    public void TargetShowsTheServerAPackageRunnerNames()
    {
        Assert.Equal("uvx mcp-server-fetch", ConnectorTarget.TargetOf(LocalConnector("uvx", "mcp-server-fetch").Config, "/Users/x"));
        Assert.Equal("python mcp_server.py", ConnectorTarget.TargetOf(LocalConnector("python", "mcp_server.py").Config, "/Users/x"));
    }

    /// <summary>A bare hyphenated word anywhere but the server slot is as likely a password as a package.</summary>
    [Fact]
    public void TargetLeavesOutABareHyphenatedWordOutsideTheServerSlot()
    {
        AssertTarget(LocalConnector("tool", "hunter-2"), "tool", "hunter-2");
        AssertTarget(LocalConnector("tool", "-p", "s3cr3t-pass"), "tool", "s3cr3t");
        AssertTarget(LocalConnector("tool", "correct-horse-battery-staple"), "tool", "horse");
        AssertTarget(LocalConnector("uvx", "--from", "x", "my-server", "extra-word"), "uvx", "extra-word");
    }

    [Fact]
    public void TargetLeavesOutThePwFlagsValue() => AssertTarget(LocalConnector("tool", "--pw", "a.b"), "tool", "a.b");

    /// <summary>
    /// The server slot is the first positional argument, whatever it holds: a bare word there is
    /// shown because it cannot be told from a package name, and only there.
    /// </summary>
    [Fact]
    public void TargetTrustsOnlyTheFirstPositionalAfterAPackageRunner()
    {
        Assert.Equal("npx hunter-2 …/pkg",
            ConnectorTarget.TargetOf(LocalConnector("npx", "-y", "hunter-2", "@scope/pkg", "other-word").Config, "/Users/x"));
    }

    /// <summary>
    /// Claude Desktop on Windows runs a server through <c>cmd /c</c>: what cmd runs is the launcher,
    /// named without its <c>.cmd</c>, and a bridge spelled that way is still a remote connector.
    /// </summary>
    [Fact]
    public void TargetUnwrapsCmdAndAWindowsLaunchersExtension()
    {
        Assert.Equal(@"npx …/server-filesystem ~\Docs",
            ConnectorTarget.TargetOf(LocalConnector("cmd", "/c", "npx", "-y", "@modelcontextprotocol/server-filesystem", @"C:\Users\x\Docs").Config,
                @"C:\Users\x"));
        Assert.Equal("npx mcp-server-fetch", ConnectorTarget.TargetOf(LocalConnector("npx.cmd", "-y", "mcp-server-fetch").Config, "/Users/x"));
        AssertTarget(LocalConnector("cmd", "/c", "tool", "--token", "abc"), "tool", "abc");
        AssertTarget(LocalConnector("CMD.EXE", "/K", "npx", "-y", "mcp-remote", "https://h.example/mcp", "--header", "Authorization: Bearer abc"),
            "h.example", "abc");
    }

    /// <summary>
    /// A launcher that could itself be a secret is left out, and the arguments still show; a
    /// command line's words after its first are arguments, held to the rule like any other.
    /// </summary>
    [Fact]
    public void TargetLeavesOutALauncherThatCouldBeASecret()
    {
        AssertTarget(LocalConnector("TOKEN=abc node", "srv.js"), "srv.js", "abc");
        AssertTarget(LocalConnector("ghp_0123456789abcdef0123456789abcdef", "srv.js"), "srv.js", "ghp_");
    }
}

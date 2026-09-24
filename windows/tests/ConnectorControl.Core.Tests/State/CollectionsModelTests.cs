using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// Tests/ConnectorControlStateTests/CollectionsModelTests.swift. The two panes of the Collections
/// window: the collections as items, the selected one's connectors as rows, the toolbar's
/// enablement, and the actions that go through the dialog seam.
/// </summary>
public class CollectionsModelTests
{
    private static CollectionsFile.Entry Synced(string fileName) => new(CollectionKind.Synced, fileName);

    private static CollectionsFile.Entry Published(string slug) =>
        new(CollectionKind.Local, publish: new CollectionsFile.PublishRecord(slug, "origin", PublishIntent.None));

    private static CollectionsLocalCache.SyncedBinding Bound(string? path) => new(path, null, []);

    private static CollectionsFile File_(params (string Name, CollectionsFile.Entry Entry)[] entries) =>
        new(entries.Select(e => new KeyValuePair<string, CollectionsFile.Entry>(e.Name, e.Entry)));

    private static CollectionsLocalCache Cache(
        IEnumerable<KeyValuePair<string, CollectionsLocalCache.SyncedBinding>>? synced = null,
        IEnumerable<KeyValuePair<string, CollectionsLocalCache.PublishBinding>>? published = null) =>
        new(synced ?? [], published ?? []);

    /// <summary>
    /// Writes both collection files where the app reads them, then reloads so the state picks them
    /// up — the shape a subscribe or a publish would leave behind.
    /// </summary>
    private static void Seed(AppStateHarness h, AppState state, CollectionsFile file, CollectionsLocalCache? cache = null)
    {
        file.Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        (cache ?? Cache()).Save(state.Service.Paths.CollectionsCachePath);
        state.Reload();
    }

    private static McpEntry Local(string command, params string[] args) =>
        new(JsonValue.Object(
            ("command", JsonValue.String(command)),
            ("args", JsonValue.Array(args.Select(JsonValue.String)))));

    /// <summary>
    /// Leaves a refusal in LastError without moving the window: the store refuses an empty name
    /// before it renames anything.
    /// </summary>
    private static void PresetError(CollectionsModel model, AppStateHarness h)
    {
        h.Dialogs.NextPromptAnswer = "";
        model.Rename();
        Assert.NotNull(model.LastError);
    }

    private static Dictionary<string, CollectionDiff> Pending(string collection) =>
        new(StringComparer.Ordinal) { [collection] = new CollectionDiff(["jira"], [], []) };

    // MARK: items

    [Fact]
    public void ItemsMirrorTheStoreAndMarkSyncedPublishedAndPending()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Shared"));
        Assert.Null(state.CreateCollection("Team"));
        state.SwitchCollection("Default");
        var file = File_(("Shared", Published("shared")), ("Team", Synced("team.json")));
        var cache = Cache([new("Team", Bound("/shared/team.json"))],
                          [new("Shared", new CollectionsLocalCache.PublishBinding("/tmp/share", null))]);
        Seed(h, state, file, cache);

        using var model = new CollectionsModel(state, h.Dialogs);
        Assert.Equal(["Default", "Shared", "Team"], model.Items.Select(i => i.Name));   // the chip menu's order
        Assert.Equal(["Default", "Shared", "Team"], model.Items.Select(i => i.Id));
        Assert.Equal([CollectionKind.Local, CollectionKind.Local, CollectionKind.Synced], model.Items.Select(i => i.Kind));
        Assert.Equal([true, false, false], model.Items.Select(i => i.IsActive));
        Assert.Equal([false, true, false], model.Items.Select(i => i.IsPublished));
        Assert.Equal([false, false, false], model.Items.Select(i => i.HasPendingUpdate));
        // A local collection has no file to find.
        Assert.Equal([true, true, true], model.Items.Select(i => i.IsLocated));

        // The republish is what repaints the view, and it is the one line a passthrough cannot prove.
        var repaints = 0;
        model.PropertyChanged += (_, _) => repaints++;
        state.PendingUpdates = Pending("Team");
        Assert.Equal([false, false, true], model.Items.Select(i => i.HasPendingUpdate));
        Assert.True(repaints > 0);

        // The binding gone, the sidecar still names the file: the item says it is not located.
        Seed(h, state, file, Cache(published: cache.Published));
        Assert.Equal([true, true, false], model.Items.Select(i => i.IsLocated));
        Assert.False(state.IsLocated("Team"));
        Assert.True(state.IsLocated("Default"));

        // Dispose cuts the republish: nothing repaints. What the kept list holds afterwards is not
        // asserted — nobody reads it once the window is gone, and pinning a stale value would make
        // a behaviour of it.
        model.Dispose();
        var before = repaints;
        state.PendingUpdates = new Dictionary<string, CollectionDiff>(StringComparer.Ordinal);
        Assert.Equal(before, repaints);
    }

    // MARK: rows

    [Fact]
    public void RowsForASyncedCollectionAreLockedAndUncheckable()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        Assert.Null(state.Upsert("github", new McpEntry(AppStateHarness.Remote("https://github.example/mcp")), null, "Team"));
        Assert.Null(state.Upsert("Ledger", Local("/usr/local/bin/node", "index.js"), null, "Team"));
        Assert.Null(state.Upsert("jira", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String("npx")),
            ("env", JsonValue.Object(("JIRA_TOKEN", JsonValue.String(Placeholder.Marker("JIRA_TOKEN"))))))), null, "Team"));
        Assert.Null(state.Upsert("notes", Local("uvx"), null, "Default"));
        Seed(h, state, File_(("Team", Synced("team.json"))), Cache([new("Team", Bound("/shared/team.json"))]));

        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Team";
        // Uppercase first: ordinal, the order the flyout lists the same connectors in.
        Assert.Equal(["Ledger", "github", "jira"], model.Rows.Select(r => r.Name));
        Assert.Equal(["Ledger", "github", "jira"], model.Rows.Select(r => r.Id));
        Assert.Equal(["node index.js", "github.example", "npx"], model.Rows.Select(r => r.Target));
        // Every row of a synced collection carries the lock.
        Assert.All(model.Rows, r => Assert.True(r.IsLocked));
        Assert.Equal([null, null, AppState.NeedsValueCaution("JIRA_TOKEN")], model.Rows.Select(r => r.Caution));
        Assert.Equal([true, true, true], model.Rows.Select(r => r.Enabled));

        // Nothing in a synced collection can be exported, so nothing in one can be ticked.
        model.SetChecked("github", true);
        Assert.All(model.Rows, r => Assert.False(r.Checked));
        Assert.Empty(model.CheckedNames);
        Assert.Empty(model.ExportIntentForChecked());
        Assert.False(model.CanExport);

        // The same rows in a local collection do tick, and the ticks belong to that collection.
        model.Selected = "Default";
        Assert.Equal(["notes"], model.Rows.Select(r => r.Name));
        Assert.Equal(["uvx"], model.Rows.Select(r => r.Target));
        Assert.All(model.Rows, r => Assert.False(r.IsLocked));
        model.SetChecked("notes", true);
        Assert.Equal([true], model.Rows.Select(r => r.Checked));
        Assert.Equal(["notes"], model.CheckedNames);
        Assert.Equal(["notes"], model.ExportIntentForChecked());
        Assert.True(model.CanExport);
        model.SetChecked("notes", false);
        Assert.False(model.CanExport);

        // The pencil opens the row in the collection the window is showing, not the active one.
        model.Selected = "Team";
        var target = model.EditTargetFor("jira");
        Assert.Equal("Team", target.Collection);
        Assert.Equal("jira", target.Name);
        Assert.False(target.IsNew);
        Assert.Equal(state.Store.Collections["Team"].Mcps["jira"].Config, target.Entry.Config);

        // One connector list must not appear in two orders: the window lists the active
        // collection's rows exactly as the flyout does.
        state.SwitchCollection("Team");
        using var flyout = new FlyoutModel(state, h.Settings);
        Assert.Equal(flyout.Rows.Select(r => r.Name), model.Rows.Select(r => r.Name));
    }

    // MARK: target column

    /// <summary>The target a local connector shows, which must not carry <paramref name="secret"/> whatever else it says.</summary>
    private static void AssertTarget(McpEntry entry, string expected, string secret)
    {
        var target = CollectionsModel.TargetOf(entry.Config, "/Users/x");
        Assert.DoesNotContain(secret, target);
        Assert.Equal(expected, target);
    }

    [Fact]
    public void TargetShowsARemoteConnectorsHostOnly()
    {
        Assert.Equal("api.githubcopilot.com",
            CollectionsModel.TargetOf(AppStateHarness.Remote("https://u:p@api.githubcopilot.com/mcp?k=v"), "/Users/x"));
        Assert.Equal("localhost:8080", CollectionsModel.TargetOf(AppStateHarness.Remote("http://localhost:8080/mcp"), "/Users/x"));
    }

    [Fact]
    public void TargetShowsOnlyTheHostOfARemoteConnectorWithAHeader() =>
        AssertTarget(Local("npx", "-y", "mcp-remote", "https://h.example/mcp", "--header", "Authorization: Bearer abc"),
            "h.example", "abc");

    [Fact]
    public void TargetShortensAScopedPackageAndTheHomeFolder()
    {
        Assert.Equal("npx …/server-filesystem ~/Documents",
            CollectionsModel.TargetOf(Local("npx", "-y", "@modelcontextprotocol/server-filesystem", "/Users/x/Documents").Config, "/Users/x"));
    }

    /// <summary>
    /// A connector authored on Windows abbreviates its home folder the same way, whichever
    /// separator follows it and however its letters are cased.
    /// </summary>
    [Fact]
    public void TargetShortensAWindowsHomeFolder()
    {
        Assert.Equal(@"node ~\srv\index.js …/pkg@1.2 ~\a.js",
            CollectionsModel.TargetOf(Local("node", @"C:\Users\x\srv\index.js", "@scope/pkg@1.2", @"c:\users\X\a.js").Config,
                @"C:\Users\x"));
    }

    [Fact]
    public void TargetShowsAUrlArgumentsSchemeAndHostOnly() =>
        AssertTarget(Local("tool", "https://me:pw@h.example:8443/x?token=t#frag"), "tool https://h.example:8443", "pw");

    [Fact]
    public void TargetLeavesOutASlackWebhooksPath() =>
        AssertTarget(Local("tool", "https://hooks.slack.com/services/T000/B000/XXXXsecret"), "tool https://hooks.slack.com", "XXXXsecret");

    [Fact]
    public void TargetLeavesOutFlagsAndTheirValues() =>
        AssertTarget(Local("/usr/local/bin/tool", "--api-key", "abc", "--port", "80", "--token=abc"), "tool", "abc");

    [Fact]
    public void TargetLeavesOutAHeaderFlag() =>
        AssertTarget(Local("tool", "-H", "X-Api-Key: abc", "https://h.example/x"), "tool https://h.example", "abc");

    [Fact]
    public void TargetLeavesOutAnEnvironmentAssignment() =>
        AssertTarget(Local("docker", "run", "-i", "--rm", "-e", "GITHUB_TOKEN=abc", "ghcr.io/github/github-mcp-server"),
            "docker ghcr.io/github/github-mcp-server", "abc");

    [Fact]
    public void TargetLeavesOutAShellString()
    {
        AssertTarget(Local("sh", "-c", "TOKEN=abc node srv.js"), "sh", "abc");
        AssertTarget(Local("sh", "-c", "curl -H 'Authorization: Bearer abc' https://h.example/x"), "sh", "abc");
    }

    [Fact]
    public void TargetLeavesOutAnAttachedShortFlagValue() => AssertTarget(Local("mysql-mcp", "-pSECRET"), "mysql-mcp", "SECRET");

    [Fact]
    public void TargetLeavesOutAShortPositionalSecret() => AssertTarget(Local("tool", "hunter2"), "tool", "hunter2");

    [Fact]
    public void TargetLeavesOutAnUnprefixedKey() => AssertTarget(Local("tool", "sk_live_abc123"), "tool", "sk_live");

    [Fact]
    public void TargetLeavesOutAConnectionString() => AssertTarget(Local("tool", "Server=h;Password=x"), "tool", "Password=x");

    [Fact]
    public void TargetLeavesOutInlineJson() => AssertTarget(Local("tool", "--config", """{"apiKey":"abc"}"""), "tool", "abc");

    [Fact]
    public void TargetLeavesOutTheValueOfASecretNamedFlagWhateverItsCase()
    {
        AssertTarget(Local("tool", "--token", "x.y"), "tool", "x.y");
        AssertTarget(Local("tool", "--TOKEN", "abc.def"), "tool", "abc.def");
    }

    /// <summary>
    /// A command line written as one string is split into launcher and arguments and each word
    /// held to the rule, so its last word — often a flag's value — is never shown for the launcher.
    /// </summary>
    [Fact]
    public void TargetSplitsACommandLineWrittenAsOneString()
    {
        AssertTarget(Local("cmd", "/c", "npx -y @acme/server --api-key hunter2"), "npx …/server", "hunter2");
        AssertTarget(new McpEntry(JsonValue.Object(("command", JsonValue.String("npx -y server --token hunter2")))), "npx", "hunter2");
        AssertTarget(new McpEntry(JsonValue.Object(("command", JsonValue.String("tool --token a.b")))), "tool", "a.b");
        const string quoted = "npx -y mcp-remote https://h.example/mcp --header \"Authorization: Bearer abc\"";
        AssertTarget(Local("cmd", "/c", quoted), "h.example", "abc");
        Assert.DoesNotContain("Bearer", CollectionsModel.TargetOf(Local("cmd", "/c", quoted).Config, "/Users/x"));
    }

    /// <summary>
    /// A quoted argument in a one-string command line — a header value, some JSON — is one
    /// argument, and one holding whitespace fails every shape the column shows; an unterminated
    /// quote runs to the end as part of its word.
    /// </summary>
    [Fact]
    public void TargetLeavesOutAQuotedArgumentInACommandLine()
    {
        AssertTarget(Local("cmd", "/c", "tool --header \"X-Key: abc.def extra\""), "tool", "abc.def");
        AssertTarget(Local("cmd", "/c", "npx -y @acme/server --config '{\"k\":\"v.w\"}'"), "npx …/server", "v.w");
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
        AssertTarget(Local(@"C:\Program Files\nodejs\node.exe", "index.js"), "node index.js", "Program");
        AssertTarget(Local(@"""C:\Program Files\nodejs\node.exe""", "index.js"), "node index.js", "Program");
        AssertTarget(Local(@"C:\Program Files\nodejs\npx.cmd", "-y", "mcp-remote", "https://h.example/mcp"), "h.example", "npx");
        AssertTarget(Local("/opt/bin/tool --password hunter.2x", "srv.js"), "srv.js", "hunter.2x");
        AssertTarget(Local(@"C:\Program Files (x86)\Tool\tool.exe", "index.js"), "tool index.js", "Program");
        // Plain words packed after a Unix path do not cost it its launcher.
        AssertTarget(Local("/usr/bin/tool srv"), "tool", "srv");
    }

    /// <summary>
    /// A path command with a flag, a URL or a switch packed into it names no launcher: its last
    /// component could be the tail of an argument.
    /// </summary>
    [Fact]
    public void TargetNamesNoLauncherForAPathCommandWithAPackedArgument()
    {
        AssertTarget(Local("cmd", "/c", @"C:\tools\notify.exe https://hooks.slack.com/services/T000/B000/XXXXsecret"), "", "XXXXsecret");
        AssertTarget(Local("/usr/local/bin/mcp --api-key abc/hunter.2x"), "", "hunter.2x");
        AssertTarget(Local(@"C:\x\tool.exe --token ab\cd.ef"), "", "cd.ef");
    }

    /// <summary>
    /// A flag named for a secret at the end of a command guards the first argument after it,
    /// whether the command is a path or a tokenized line.
    /// </summary>
    [Fact]
    public void TargetLeavesOutTheFirstArgumentAfterASecretNamedFlagInTheCommand()
    {
        AssertTarget(Local("/opt/bin/tool --password", "hunter.2x"), "", "hunter.2x");
        AssertTarget(Local("tool --token", "abc.def"), "tool", "abc.def");
    }

    /// <summary>
    /// A quote packed into a path command costs it its launcher, and a quoted or partly quoted
    /// flag at its end is still a flag named for a secret.
    /// </summary>
    [Fact]
    public void TargetReadsAQuotedFlagPackedIntoAPathCommand()
    {
        AssertTarget(Local(@"/opt/bin/tool ""--password""", "hunter.2x"), "", "hunter.2x");
        AssertTarget(Local(@"C:\x\tool.exe ""--token"" ab\cd.ef"), "", "cd.ef");
        AssertTarget(Local(@"/opt/bin/tool --to""ken""", "hunter.2x"), "", "hunter.2x");
    }

    /// <summary>
    /// Each check that keeps a path command plain is load-bearing: a Windows switch and an
    /// assignment packed in both cost it its launcher.
    /// </summary>
    [Fact]
    public void TargetNamesNoLauncherForAPathCommandWithASwitchOrAnAssignment()
    {
        AssertTarget(Local(@"C:\x\tool.exe /key ab\cd.ef"), "", "cd.ef");
        AssertTarget(Local("/opt/bin/tool key=ab/cd.ef"), "", "cd.ef");
    }

    /// <summary>
    /// A password holding an unencoded <c>/</c>, <c>?</c> or <c>#</c> ends the authority early; what
    /// is left of the userinfo is refused as a host rather than shown, and so is a scheme that is not one.
    /// </summary>
    [Fact]
    public void TargetRefusesAUrlWhoseUserinfoHoldsADelimiter()
    {
        AssertTarget(Local("tool", "postgres://admin:hunter2#x@db.local/app"), "tool", "hunter2");
        AssertTarget(Local("tool", "https://apikey:sk_live_abc/x@api.example.com"), "tool", "sk_live");
        AssertTarget(Local("tool", "https://sk_live_abc/x@h"), "tool", "sk_live");
        AssertTarget(Local("tool", "sk-proj-abc123://x"), "tool", "abc123");
        AssertTarget(Local("tool", "mongodb+srv://u:p@cluster.example.net/db"), "tool mongodb+srv://cluster.example.net", "u:p");
        // The remote decoder accepts this URL, and its host is still checked.
        AssertTarget(Local("npx", "-y", "mcp-remote", "https://token123/x@h.example/mcp"), CollectionsModel.RemoteType, "token123");
    }

    /// <summary>
    /// A long random token is left out even with a <c>/</c> in it, which <c>LooksLikeCredential</c>
    /// would not consider; paths and names with a dot, a hyphen or no digits are kept.
    /// </summary>
    [Fact]
    public void TargetLeavesOutARandomTokenEvenWithASlash()
    {
        AssertTarget(Local("tool", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"), "tool", "wJalr");
        Assert.Equal("tool ~/Documents ghcr.io/github/github-mcp-server ./build/v2Server",
            CollectionsModel.TargetOf(Local("tool", "/Users/x/Documents", "ghcr.io/github/github-mcp-server", "./build/v2Server").Config,
                "/Users/x"));
    }

    /// <summary>A Windows switch's value is not a path: a colon anywhere but a drive letter's drops it.</summary>
    [Fact]
    public void TargetLeavesOutAWindowsSwitchesValue()
    {
        AssertTarget(Local("tool", "/p:Hunter2", "/token:abc", "/x:secret"), "tool", "secret");
        AssertTarget(Local("tool", @"C:\Users\x\Docs"), @"tool C:\Users\x\Docs", "secret");
    }

    /// <summary>A secret shaped like a package still vanishes when a flag named for a secret precedes it.</summary>
    [Fact]
    public void TargetLeavesOutWhateverFollowsASecretNamedFlag() =>
        AssertTarget(Local("tool", "--password", "s3cr3t-pass", "--pass", "./x.key", "index.js"), "tool index.js", "s3cr3t");

    [Fact]
    public void TargetLeavesOutAPathCarryingAnAssignment() => AssertTarget(Local("tool", "/usr/bin/env TOKEN=abc"), "tool", "abc");

    [Fact]
    public void TargetLeavesOutACredentialShapedArtefact() =>
        AssertTarget(Local("tool", "sk-abc.def", "ghp_0123456789abcdef0123456789abcdef.js"), "tool", "abc");

    [Fact]
    public void TargetShowsTheServerAPackageRunnerNames()
    {
        Assert.Equal("uvx mcp-server-fetch", CollectionsModel.TargetOf(Local("uvx", "mcp-server-fetch").Config, "/Users/x"));
        Assert.Equal("python mcp_server.py", CollectionsModel.TargetOf(Local("python", "mcp_server.py").Config, "/Users/x"));
    }

    /// <summary>A bare hyphenated word anywhere but the server slot is as likely a password as a package.</summary>
    [Fact]
    public void TargetLeavesOutABareHyphenatedWordOutsideTheServerSlot()
    {
        AssertTarget(Local("tool", "hunter-2"), "tool", "hunter-2");
        AssertTarget(Local("tool", "-p", "s3cr3t-pass"), "tool", "s3cr3t");
        AssertTarget(Local("tool", "correct-horse-battery-staple"), "tool", "horse");
        AssertTarget(Local("uvx", "--from", "x", "my-server", "extra-word"), "uvx", "extra-word");
    }

    [Fact]
    public void TargetLeavesOutThePwFlagsValue() => AssertTarget(Local("tool", "--pw", "a.b"), "tool", "a.b");

    /// <summary>
    /// The server slot is the first positional argument, whatever it holds: a bare word there is
    /// shown because it cannot be told from a package name, and only there.
    /// </summary>
    [Fact]
    public void TargetTrustsOnlyTheFirstPositionalAfterAPackageRunner()
    {
        Assert.Equal("npx hunter-2 …/pkg",
            CollectionsModel.TargetOf(Local("npx", "-y", "hunter-2", "@scope/pkg", "other-word").Config, "/Users/x"));
    }

    /// <summary>
    /// Claude Desktop on Windows runs a server through <c>cmd /c</c>: what cmd runs is the launcher,
    /// named without its <c>.cmd</c>, and a bridge spelled that way is still a remote connector.
    /// </summary>
    [Fact]
    public void TargetUnwrapsCmdAndAWindowsLaunchersExtension()
    {
        Assert.Equal(@"npx …/server-filesystem ~\Docs",
            CollectionsModel.TargetOf(Local("cmd", "/c", "npx", "-y", "@modelcontextprotocol/server-filesystem", @"C:\Users\x\Docs").Config,
                @"C:\Users\x"));
        Assert.Equal("npx mcp-server-fetch", CollectionsModel.TargetOf(Local("npx.cmd", "-y", "mcp-server-fetch").Config, "/Users/x"));
        AssertTarget(Local("cmd", "/c", "tool", "--token", "abc"), "tool", "abc");
        AssertTarget(Local("CMD.EXE", "/K", "npx", "-y", "mcp-remote", "https://h.example/mcp", "--header", "Authorization: Bearer abc"),
            "h.example", "abc");
    }

    /// <summary>
    /// A launcher that could itself be a secret is left out, and the arguments still show; a
    /// command line's words after its first are arguments, held to the rule like any other.
    /// </summary>
    [Fact]
    public void TargetLeavesOutALauncherThatCouldBeASecret()
    {
        AssertTarget(Local("TOKEN=abc node", "srv.js"), "srv.js", "abc");
        AssertTarget(Local("ghp_0123456789abcdef0123456789abcdef", "srv.js"), "srv.js", "ghp_");
    }

    /// <summary>
    /// End to end through the rows: a connector carrying secrets five ways shows none of them.
    /// (The harness's seeded fixture holds no secrets, so this plants its own.)
    /// </summary>
    [Fact]
    public void NoRowTargetCarriesASecret()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        string[] secrets = ["ghp_0123456789abcdef0123456789abcdef", "s3cr3t-pass", "tok123", "hdrsecret", "kvsecret"];
        var entry = new McpEntry(JsonValue.Object(
            ("command", JsonValue.String("/opt/bin/tool")),
            ("args", JsonValue.Array(new[]
            {
                "--password", secrets[1], $"--token={secrets[2]}", secrets[0],
                "--header", $"Authorization: Bearer {secrets[3]}", "-e", $"DB_PASSWORD={secrets[4]}",
            }.Select(JsonValue.String))),
            ("env", JsonValue.Object(("API_KEY", JsonValue.String("envsecret"))))));
        Assert.Null(state.Upsert("leaky", entry, null, "Default"));
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Default";
        var target = Assert.Single(model.Rows, r => r.Name == "leaky").Target;
        Assert.Equal("tool", target);
        foreach (var secret in secrets.Append("envsecret"))
        {
            Assert.DoesNotContain(secret, target);
        }
    }

    // MARK: toolbar

    [Fact]
    public void ToolbarEnablementFollowsTheSelection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Shared"));
        Assert.Null(state.CreateCollection("Team"));
        state.SwitchCollection("Default");
        var file = File_(("Shared", Published("shared")), ("Team", Synced("team.json")));
        var located = Cache([new("Team", Bound("/shared/team.json"))],
                            [new("Shared", new CollectionsLocalCache.PublishBinding("/tmp/share", null))]);
        Seed(h, state, file, located);

        using var model = new CollectionsModel(state, h.Dialogs);
        Assert.Equal("Default", model.Selected);   // the selection starts on the active collection
        Assert.False(model.CanExport);             // nothing is ticked yet
        model.SetChecked("aws-mcp", true);
        Assert.True(model.CanExport);
        Assert.True(model.CanPublish);
        Assert.False(model.CanRefresh);
        Assert.True(model.CanDelete);

        model.Selected = "Shared";
        Assert.False(model.CanExport);    // the ticks belonged to the collection that was showing
        // Published from here, and still offered: reopening the dialog shows the record and
        // pressing Publish again updates what is shared, so both links stand side by side.
        Assert.True(model.CanPublish);
        Assert.False(model.CanRefresh);
        Assert.True(model.CanDelete);

        model.Selected = "Team";
        Assert.False(model.CanExport);
        Assert.False(model.CanPublish);   // a synced collection has an author elsewhere
        Assert.True(model.CanRefresh);
        // A synced collection goes without taking the last local one with it.
        Assert.True(model.CanDelete);

        // Nothing to refresh until the file is found on this machine.
        Seed(h, state, file, Cache(published: located.Published));
        Assert.False(model.CanRefresh);

        // With the second local collection gone, the last one cannot be deleted.
        Assert.Null(state.DeleteCollection("Shared"));
        model.Selected = "Default";
        Assert.False(model.CanDelete);
        model.Selected = "Team";
        Assert.True(model.CanDelete);

        // Nor can the last collection of any kind: the store always has an active one, so a lone
        // synced collection is no more deletable than a lone local one.
        Assert.Null(state.DeleteCollection("Team"));
        Seed(h, state, File_(("Default", Synced("default.json"))),
            Cache([new("Default", Bound("/shared/default.json"))]));
        Assert.Equal(["Default"], state.CollectionNames);
        Assert.True(state.IsSynced("Default"));
        Assert.False(model.CanDelete);
    }

    // MARK: create, rename, delete

    /// <summary>
    /// A cancelled prompt, or a verb that finds nothing to act on, leaves no error behind: the
    /// window shows LastError after every verb, so one left over from an earlier failure would
    /// come back as if this verb had failed. A declined confirmation counts as a cancel: it clears
    /// the last error too, and changes nothing else.
    /// </summary>
    [Fact]
    public void AVerbThatDoesNothingClearsTheLastError()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        Assert.Null(state.Upsert("alpha", Local("/bin/alpha"), null, "Default"));
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Default";

        void LeavesNoError(string verb, Action action)
        {
            PresetError(model, h);
            h.Dialogs.NextPromptAnswer = null;
            action();
            Assert.True(model.LastError is null, $"{verb} brought the earlier error back");
        }

        // Cancelled prompts.
        LeavesNoError("Create", model.Create);
        LeavesNoError("Rename", model.Rename);
        LeavesNoError("Duplicate", () => model.Duplicate());

        // Nothing to act on.
        LeavesNoError("MakeLocalCopy of a local collection", model.MakeLocalCopy);
        LeavesNoError("StopPublishing of a collection that does not publish", model.StopPublishing);
        LeavesNoError("CopyChecked with nothing ticked", () => model.CopyChecked("Team"));
        LeavesNoError("CopyCheckedIntoNewCollection with nothing ticked", () => model.CopyCheckedIntoNewCollection());
        LeavesNoError("RemoveChecked with nothing ticked", model.RemoveChecked);

        model.SetChecked("alpha", true);
        LeavesNoError("CopyChecked into a collection it does not offer", () => model.CopyChecked("Default"));
        LeavesNoError("CopyCheckedIntoNewCollection, cancelled", () => model.CopyCheckedIntoNewCollection());
        Assert.Equal(["alpha"], model.CheckedNames);   // nothing was copied, so the ticks stay

        Seed(h, state, File_(("Team", Synced("team.json"))));
        model.Selected = "Team";
        LeavesNoError("MakeLocalCopy, cancelled", model.MakeLocalCopy);
        Assert.Equal(["Default", "Team"], state.CollectionNames);   // no prompt that was cancelled made anything
    }

    [Fact]
    public void CreateRenameDeleteGoThroughTheDialogs()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Work"));
        using var model = new CollectionsModel(state, h.Dialogs);
        Assert.Equal("Work", model.Selected);

        // A cancelled prompt does nothing at all.
        h.Dialogs.NextPromptAnswer = null;
        model.Create();
        Assert.Equal(new FakeDialogs.PromptCall(AppState.NewCollectionTitle, ""), h.Dialogs.Prompts[^1]);
        Assert.Equal(["Default", "Work"], state.CollectionNames);
        Assert.Null(model.LastError);

        h.Dialogs.NextPromptAnswer = "  Team  ";
        model.Create();
        Assert.Equal(["Default", "Team", "Work"], state.CollectionNames);
        Assert.Equal("Team", model.Selected);   // the window shows what it just made
        Assert.Null(model.LastError);

        // A name the store refuses comes back as the model's error.
        h.Dialogs.NextPromptAnswer = "Work";
        model.Create();
        Assert.NotNull(model.LastError);
        Assert.Equal(["Default", "Team", "Work"], state.CollectionNames);

        h.Dialogs.NextPromptAnswer = "Team B";
        model.Rename();
        Assert.Equal(new FakeDialogs.PromptCall(AppState.RenameCollectionTitle, "Team"), h.Dialogs.Prompts[^1]);
        Assert.Equal(["Default", "Team B", "Work"], state.CollectionNames);
        Assert.Equal("Team B", model.Selected);   // the selection follows the name it just gave
        Assert.Null(model.LastError);             // a successful action clears the last one's error

        // Declined: the collection stays, and the earlier error does not come back.
        PresetError(model, h);
        h.Dialogs.NextConfirm = false;
        model.Delete();
        var asked = h.Dialogs.Confirms[^1];
        Assert.Equal(AppState.DeleteCollectionMessage("Team B"), asked.Message);
        Assert.Equal(AppState.DeleteButton, asked.Primary);
        Assert.True(asked.Destructive);
        Assert.Equal(["Default", "Team B", "Work"], state.CollectionNames);
        Assert.Null(model.LastError);   // a declined confirmation clears the last error

        h.Dialogs.NextConfirm = true;
        model.Delete();
        Assert.Equal(["Default", "Work"], state.CollectionNames);
        Assert.Equal(2, h.Dialogs.Confirms.Count);   // an unpublished collection is asked about once
        Assert.Equal(state.ActiveCollection, model.Selected);
    }

    [Fact]
    public void StopSyncingConfirmsAndADeclineLeavesTheCollectionSynced()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        state.SwitchCollection("Default");
        Seed(h, state, File_(("Team", Synced("team.json"))), Cache([new("Team", Bound("/shared/team.json"))]));
        Assert.True(state.IsSynced("Team"));
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Team";

        // Declined: the collection is still synced, and the earlier error does not come back.
        // The reassurance lives in the question now that the button no longer carries it.
        PresetError(model, h);
        h.Dialogs.NextConfirm = false;
        model.StopSyncing();
        var asked = h.Dialogs.Confirms[^1];
        Assert.Equal(CollectionsModel.StopSyncingMessage("Team"), asked.Message);
        Assert.Equal(CollectionsModel.StopSyncingInformative, asked.Informative);
        Assert.Equal(CollectionsModel.StopSyncingAction, asked.Primary);
        Assert.False(asked.Destructive);   // nothing is lost: every connector stays
        Assert.True(state.IsSynced("Team"));
        Assert.Null(model.LastError);   // a declined confirmation clears the last error

        h.Dialogs.NextConfirm = true;
        model.StopSyncing();
        Assert.False(state.IsSynced("Team"));
        Assert.Equal(["Default", "Team"], state.CollectionNames);   // the collection stays, now local
        Assert.Equal(2, h.Dialogs.Confirms.Count);
    }

    [Fact]
    public void DeletingAPublishedCollectionAsksAboutTheFile()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        var folder = h.Dir.File("share");
        Directory.CreateDirectory(folder);
        Assert.Null(state.CreateCollection("Shared"));
        Assert.Null(state.CreateCollection("Consulting"));
        Assert.Null(state.StartPublishing("Shared", folder, PublishIntent.None));
        Assert.Null(state.StartPublishing("Consulting", folder, PublishIntent.None));
        var sharedFile = Path.Combine(folder, "shared.json");
        var consultingFile = Path.Combine(folder, "consulting.json");
        Assert.True(File.Exists(sharedFile));
        Assert.True(File.Exists(consultingFile));

        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Shared";
        h.Dialogs.ConfirmAnswers.Enqueue(true);    // delete the collection
        h.Dialogs.ConfirmAnswers.Enqueue(false);   // keep the document
        model.Delete();
        Assert.Equal(
            [AppState.DeleteCollectionMessage("Shared"), CollectionsModel.DeletePublishedFileQuestion("shared.json")],
            h.Dialogs.Confirms.Select(c => c.Message));
        var fileQuestion = h.Dialogs.Confirms[^1];
        Assert.Equal(CollectionsModel.RemoveFileButton, fileQuestion.Primary);
        Assert.Equal(CollectionsModel.KeepFileButton, fileQuestion.Cancel);
        Assert.False(fileQuestion.Destructive);
        Assert.DoesNotContain("Shared", state.CollectionNames);
        // Keep leaves the copy the team reads where it is.
        Assert.True(File.Exists(sharedFile));

        // The same question on its own, answered the other way.
        model.Selected = "Consulting";
        h.Dialogs.ConfirmAnswers.Enqueue(true);
        model.StopPublishing();
        Assert.Equal(CollectionsModel.DeletePublishedFileQuestion("consulting.json"), h.Dialogs.Confirms[^1].Message);
        Assert.False(File.Exists(consultingFile));
        Assert.False(state.IsPublished("Consulting"));
        // Stop Publishing keeps the collection.
        Assert.Contains("Consulting", state.CollectionNames);
    }

    [Fact]
    public void AFailedPublishIsStoppedWithoutAskingAboutTheFile()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        var folder = h.Dir.File("share");
        Directory.CreateDirectory(folder);
        Assert.Null(state.CreateCollection("Shared"));
        Assert.Null(state.StartPublishing("Shared", folder, PublishIntent.None));
        var file = Path.Combine(folder, "shared.json");
        Assert.True(File.Exists(file));

        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Shared";
        // The folder that refused the write would refuse the delete, so Remove is not offered.
        state.PublishError = new CollectionPublishError("Shared", "the folder is read-only");
        model.StopPublishing();
        // Nothing to ask when Remove could not be honoured.
        Assert.Empty(h.Dialogs.Confirms);
        Assert.False(state.IsPublished("Shared"));
        // The document stays where it is.
        Assert.True(File.Exists(file));
        Assert.Null(state.PublishError);

        // Another collection's failure is not this one's, so the question comes back.
        Assert.Null(state.CreateCollection("Consulting"));
        Assert.Null(state.StartPublishing("Consulting", folder, PublishIntent.None));
        model.Selected = "Consulting";
        state.PublishError = new CollectionPublishError("Shared", "the folder is read-only");
        h.Dialogs.ConfirmAnswers.Enqueue(false);
        model.StopPublishing();
        Assert.Equal([CollectionsModel.DeletePublishedFileQuestion("consulting.json")],
                     h.Dialogs.Confirms.Select(c => c.Message));
    }

    // MARK: toggles

    [Fact]
    public void SetEnabledInAnInactiveCollectionLeavesClaudesConfigAlone()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Work"));
        state.SwitchCollection("Default");
        using var model = new CollectionsModel(state, h.Dialogs);

        model.Selected = "Work";
        model.SetEnabled("aws-mcp", false);
        Assert.False(state.Store.Collections["Work"].Mcps["aws-mcp"].Enabled);
        Assert.False(h.StoreOnDisk().Collections["Work"].Mcps["aws-mcp"].Enabled);
        Assert.True(state.Store.Collections["Default"].Mcps["aws-mcp"].Enabled);
        // Claude runs the active collection, which did not change.
        Assert.True(h.ClaudeServers().ContainsKey("aws-mcp"));
        Assert.False(model.Rows.Single(r => r.Name == "aws-mcp").Enabled);

        // The same toggle in the active collection does reach Claude.
        model.Selected = "Default";
        model.SetEnabled("aws-mcp", false);
        Assert.False(h.ClaudeServers().ContainsKey("aws-mcp"));
    }

    // MARK: detail line

    [Fact]
    public void DetailLineFollowsTheCollectionState()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Shared"));
        Assert.Null(state.CreateCollection("Team"));
        state.SwitchCollection("Default");
        var file = File_(("Shared", Published("shared")), ("Team", Synced("team.json")));
        var located = Cache([new("Team", Bound("/shared/team.json"))],
                            [new("Shared", new CollectionsLocalCache.PublishBinding("/Acme/mcp", null))]);
        Seed(h, state, file, located);

        using var model = new CollectionsModel(state, h.Dialogs);
        Assert.Equal(CollectionsModel.LocalDetail(3) + CollectionsModel.ActiveSuffix, model.DetailLine);

        model.Selected = "Shared";
        Assert.Equal(CollectionsModel.LocalDetail(3) + " · " + CollectionsModel.PublishedDetail("/Acme/mcp"), model.DetailLine);

        model.Selected = "Team";
        Assert.Equal(CollectionsModel.SyncedDetail("/shared/team.json", CollectionsModel.UpToDateStatus), model.DetailLine);
        state.PendingUpdates = Pending("Team");
        Assert.Equal(CollectionsModel.SyncedDetail("/shared/team.json", CollectionsModel.UpdateAvailableStatus), model.DetailLine);
        state.SourceErrors = new Dictionary<string, string>(StringComparer.Ordinal) { ["Team"] = "team.json couldn’t be read" };
        // What went wrong outranks what is waiting.
        Assert.Equal(CollectionsModel.SyncedDetail("/shared/team.json", "team.json couldn’t be read"), model.DetailLine);

        // Not located: there is nothing to say about the file except that it is missing.
        Seed(h, state, file, Cache(published: located.Published));
        Assert.Equal(CollectionsModel.UnlocatedDetail, model.DetailLine);
        Assert.False(model.CanRefresh);

        // A synced entry that records no file name either — a hand-edited or foreign collections
        // file. Nothing asks to be located, but there is still no document to name or to read.
        Seed(h, state, File_(("Shared", Published("shared")), ("Team", new CollectionsFile.Entry(CollectionKind.Synced))),
            Cache(published: located.Published));
        Assert.True(state.IsLocated("Team"));   // nothing is waiting to be pointed at
        Assert.Equal(CollectionsModel.UnlocatedDetail, model.DetailLine);
        // Refresh would read a document nobody can point at.
        Assert.False(model.CanRefresh);
    }

    // MARK: selection

    [Fact]
    public void SelectionFallsBackToTheActiveCollectionWhenItsCollectionDisappears()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Work"));
        Assert.Null(state.CreateCollection("Spare"));
        state.SwitchCollection("Default");
        using var model = new CollectionsModel(state, h.Dialogs);

        model.Selected = "Work";
        Assert.Equal("Work", model.Selected);
        model.SetChecked("aws-mcp", true);
        Assert.Equal(["aws-mcp"], model.CheckedNames);

        Assert.Null(state.DeleteCollection("Work"));
        Assert.Equal("Default", model.Selected);
        Assert.Equal(state.ActiveCollection, model.Selected);
        // The ticks belonged to the collection that is gone.
        Assert.Empty(model.CheckedNames);

        // A rename anywhere else is the same disappearance: the name selected is no longer a collection.
        model.Selected = "Spare";
        Assert.Null(state.RenameCollection("Spare", "Spare Parts"));
        Assert.Equal(state.ActiveCollection, model.Selected);

        // Switching the active collection from the window goes through AppState.
        model.SwitchTo("Spare Parts");
        Assert.Equal("Spare Parts", state.ActiveCollection);
        Assert.Equal("Spare Parts", model.Items.Single(i => i.IsActive).Name);
    }

    // MARK: banner strip

    /// <summary>
    /// Default, active and published into a real folder; Team, synced with its file still to be
    /// found. The two banners the window can show therefore belong to different collections.
    /// </summary>
    private static void TwoBanners(AppStateHarness h, AppState state, string folder)
    {
        Assert.Null(state.CreateCollection("Team"));
        state.SwitchCollection("Default");
        Directory.CreateDirectory(folder);
        Seed(h, state, File_(("Team", Synced("team.json")), ("Default", Published("default"))),
             Cache(published: [new("Default", new CollectionsLocalCache.PublishBinding(folder, null))]));
    }

    [Fact]
    public void TheBannerStripSpeaksOnlyForTheSelectedCollection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = h.Dir.File("pub");
        TwoBanners(h, state, folder);
        using var model = new CollectionsModel(state, h.Dialogs);

        // Team's file is missing, but the window is showing Default: the strip says nothing.
        Assert.Equal(new CollectionBanner.Locate("Team", "team.json"), state.CollectionBanner);
        Assert.Null(model.BannerText);
        Assert.Null(model.BannerButton);
        Assert.False(model.HasBanner);
        Assert.False(model.BannerAction());

        model.Selected = "Team";
        Assert.Equal(AppState.CollectionLocateBanner("Team"), model.BannerText);
        Assert.Equal(FlyoutModel.LocateButton("team.json"), model.BannerButton);
        Assert.True(model.HasBanner);
        Assert.False(model.BannerAction());   // the view owes a file dialog

        // An update waiting is the one banner the strip can act on by itself.
        var diff = new CollectionDiff(["jira"], [], []);
        state.PendingUpdates = new Dictionary<string, CollectionDiff>(StringComparer.Ordinal) { ["Team"] = diff };
        Assert.Equal(AppState.CollectionUpdateBanner("Team", diff.Summary()), model.BannerText);
        Assert.Equal(FlyoutModel.ReviewAndApplyButton, model.BannerButton);
        Assert.True(model.BannerAction());

        // A failed publish belongs to Default, so Team's strip goes quiet again.
        var repaints = 0;
        model.PropertyChanged += (_, _) => repaints++;
        state.PublishError = new CollectionPublishError("Default", "the folder is read-only");
        Assert.True(repaints > 0, "a failed publish repaints the window");
        Assert.Null(model.BannerText);
        model.Selected = "Default";
        Assert.Equal(AppState.CollectionPublishFailedBanner("Default", folder, "the folder is read-only"),
                     model.BannerText);
        Assert.Equal(FlyoutModel.ChooseFolderButton, model.BannerButton);
        Assert.False(model.BannerAction());   // the view owes a folder dialog
    }

    [Fact]
    public void TheBannerStripLocatesAndRepointsTheSelectedCollection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var first = h.Dir.File("first");
        TwoBanners(h, state, first);
        var second = h.Dir.File("second");
        Directory.CreateDirectory(second);
        using var model = new CollectionsModel(state, h.Dialogs);

        // The window is showing Default, which the locate banner is not about.
        var document = h.Dir.File("team.json");
        System.IO.File.WriteAllBytes(document, CollectionDocumentSamples.DataTeam.Serialize());
        Assert.Null(model.LocateSource(document));
        Assert.Null(state.SourceBinding("Team"));

        model.Selected = "Team";
        Assert.Null(model.LocateSource(document));
        Assert.Equal(document, state.SourceBinding("Team")?.Path);

        // The document found, the banner has moved on, so the publish forwarding stays out of it.
        Assert.Null(model.ChoosePublishFolder(second));
        Assert.Equal(first, state.CollectionsCache.Published["Default"].Folder);

        // A failed publish belongs to Default, and only Default's window may answer it.
        state.PublishError = new CollectionPublishError("Default", "the folder is read-only");
        Assert.Null(model.ChoosePublishFolder(second));   // Team is showing, not Default
        Assert.Equal(first, state.CollectionsCache.Published["Default"].Folder);

        model.Selected = "Default";
        Assert.Null(model.ChoosePublishFolder(second));
        Assert.Equal(second, state.CollectionsCache.Published["Default"].Folder);
        // The document lands in the folder just chosen.
        Assert.True(System.IO.File.Exists(Path.Combine(second, "default.json")));
    }
    [Fact]
    public void TheWindowsGlyphsAndActionsCarryTheirOwnWords()
    {
        Assert.Equal("Edit", CollectionsModel.EditTooltip);
        Assert.Equal("Make Active", CollectionsModel.MakeActiveAction);
        Assert.Equal("Read-only: synced from the collection's author", CollectionsModel.LockedGlyphTooltip);
    }

    [Fact]
    public void TheSidebarChainNamesTheDocumentThisMachineReads()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        state.SwitchCollection("Default");
        Seed(h, state, File_(("Team", Synced("team.json"))), Cache([new("Team", Bound("/Acme/mcp/team.json"))]));
        using var model = new CollectionsModel(state, h.Dialogs);

        var team = model.Items.Single(i => i.Name == "Team");
        Assert.Equal("/Acme/mcp/team.json", team.Source);
        // One sentence about one fact: the chain says the same here as on the flyout's chip.
        Assert.Equal("Synced from /Acme/mcp/team.json", CollectionsModel.SyncedGlyphTooltip(team));
        Assert.Equal(FlyoutModel.SourceTooltipFormat("/Acme/mcp/team.json"),
                     CollectionsModel.SyncedGlyphTooltip(team));

        // A local collection has no source, so no chain and nothing to say about one.
        var local = model.Items.Single(i => i.Name == "Default");
        Assert.Null(local.Source);
        Assert.Null(CollectionsModel.SyncedGlyphTooltip(local));

        // Synced but never found: the sidecar's file name is what the chain can still name, the
        // same fallback the flyout's chip takes, so the two never disagree about one collection.
        Seed(h, state, File_(("Team", Synced("team.json"))));
        var unlocated = model.Items.Single(i => i.Name == "Team");
        Assert.False(unlocated.IsLocated);
        Assert.Equal("team.json", unlocated.Source);
        Assert.Equal("Synced from team.json", CollectionsModel.SyncedGlyphTooltip(unlocated));
        state.SwitchCollection("Team");
        using var flyout = new FlyoutModel(state, h.Settings);
        Assert.Equal(flyout.SourceTooltip, CollectionsModel.SyncedGlyphTooltip(unlocated));
    }
    [Fact]
    public void ChoosingTheCollectionAlreadyShowingChangesNothing()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Work"));
        state.SwitchCollection("Default");
        using var model = new CollectionsModel(state, h.Dialogs);
        model.SetChecked("aws-mcp", true);
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // The active collection is what is showing, so naming it again is the same selection —
        // as is naming a collection that does not exist, which falls back to the same one.
        model.Selected = "Default";
        model.Selected = null;
        model.Selected = "No such collection";
        // A view writing its selection back must not feed itself, and the ticks made in it survive.
        Assert.Empty(raised);
        Assert.Equal(["aws-mcp"], model.CheckedNames);

        // Not remembered either: the window still follows the active collection.
        state.SwitchCollection("Work");
        Assert.Equal("Work", model.Selected);

        // A real change still announces itself.
        raised.Clear();
        model.Selected = "Default";
        Assert.NotEmpty(raised);
        Assert.Equal("Default", model.Selected);
    }
    /// <summary>
    /// No write-back at all, which is the Mac's path and the only one that isolates the store-change
    /// trigger: the test below writes the selection back and so exercises the other trigger, and
    /// would still pass without this one.
    /// </summary>
    [Fact]
    public void ASelectionRenamedAwayIsForgottenWithoutAnyWriteBack()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Spare"));
        state.SwitchCollection("Default");
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Spare";

        Assert.Null(state.RenameCollection("Spare", "Spare Parts"));
        Assert.Equal("Default", model.Selected);
        Assert.Null(state.RenameCollection("Spare Parts", "Spare"));
        // A returning name must not pull the window to it.
        Assert.Equal("Default", model.Selected);
    }

    [Fact]
    public void ASelectionRenamedAwayDoesNotPullTheWindowBackWhenItReturns()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Spare"));
        state.SwitchCollection("Default");
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Spare";
        Assert.Equal("Spare", model.Selected);

        // Renamed away elsewhere: the window falls back to the active collection.
        Assert.Null(state.RenameCollection("Spare", "Spare Parts"));
        Assert.Equal("Default", model.Selected);
        // A view writing its selection back is harmless, and changes nothing either.
        model.Selected = "Default";

        // Renamed back: the name resolves again, and the window stays where the user left it.
        Assert.Null(state.RenameCollection("Spare Parts", "Spare"));
        Assert.Equal("Default", model.Selected);
    }

    /// <summary>
    /// C#-only: WPF regenerates every container when ItemsSource is handed a new list, dropping
    /// keyboard focus. SwiftUI's List diffs by id, so the Mac has no instance to keep.
    /// </summary>
    [Fact]
    public void TheSidebarKeepsItsListAcrossASelectionAndReplacesItOnlyWhenAnItemChanges()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Work"));
        Assert.Null(state.Upsert("extra", new McpEntry(AppStateHarness.Remote("https://extra.example/mcp")), null, "Work"));
        state.SwitchCollection("Default");
        using var model = new CollectionsModel(state, h.Dialogs);
        var items = model.Items;
        var rows = model.Rows;
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Choosing another collection changes the right pane and nothing in the sidebar.
        model.Selected = "Work";
        Assert.DoesNotContain(nameof(CollectionsModel.Items), raised);
        Assert.DoesNotContain(string.Empty, raised);   // no blanket raise either
        Assert.Same(items, model.Items);
        Assert.Contains(nameof(CollectionsModel.Rows), raised);
        Assert.NotSame(rows, model.Rows);
        Assert.Contains(nameof(CollectionsModel.DetailLine), raised);

        // A store change the sidebar's items do not reflect leaves the list where it is.
        raised.Clear();
        state.SetEnabled("extra", false, "Work");
        Assert.DoesNotContain(nameof(CollectionsModel.Items), raised);
        Assert.Same(items, model.Items);

        // One that alters an item — which collection is active — replaces it.
        raised.Clear();
        state.SwitchCollection("Work");
        Assert.Contains(nameof(CollectionsModel.Items), raised);
        Assert.NotSame(items, model.Items);
        Assert.True(model.Items.Single(i => i.Name == "Work").IsActive);
    }
    [Fact]
    public void TheWindowsStripOpensPublishForABlockedPublishAndStillAsksAboutTheFile()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        var folder = h.Dir.File("share");
        Directory.CreateDirectory(folder);
        Assert.Null(state.CreateCollection("Shared"));
        Assert.Null(state.StartPublishing("Shared", folder, PublishIntent.None));
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Shared";

        var moved = AppState.PathMarkMovedError("ledger");
        state.PublishError = new CollectionPublishError("Shared", moved, PublishErrorKind.BlockedForReview);
        Assert.Equal(moved, model.BannerText);
        Assert.Equal(CollectionsModel.PublishSettingsButton, model.BannerButton);   // a blocked publish is always on a collection that already publishes
        // False: true would put the Review dialog up. The view shows Publishing Settings for this kind.
        Assert.False(model.BannerAction());
        // A folder is no answer to this.
        // Refused, and the refusal says why.
        Assert.Equal(moved, model.ChoosePublishFolder(h.Dir.File("elsewhere")));
        Assert.Equal(folder, state.CollectionsCache.Published["Shared"].Folder);

        // Unlike a failed write, a blocked publish never touched the folder, so Stop Publishing can
        // still offer to remove the document there.
        h.Dialogs.ConfirmAnswers.Enqueue(false);
        model.StopPublishing();
        Assert.Equal([CollectionsModel.DeletePublishedFileQuestion("shared.json")], h.Dialogs.Confirms.Select(c => c.Message));
    }

    // MARK: selection bar

    /// <summary>Where a copy can go: local collections only, never the one the ticked rows already sit in.</summary>
    [Fact]
    public void CopyTargetsExcludeTheSourceAndEverySyncedCollection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Other"));
        // Created first: the sidecar only annotates a collection already in the master list
        // (CollectionsFile.Reconciled), so seeding "Team" with nothing to annotate would leave it
        // dropped, and excluded from CopyTargets for not existing rather than for being synced.
        Assert.Null(state.CreateCollection("Team"));
        Seed(h, state, File_(("Team", Synced("team.json"))));
        Assert.True(state.IsSynced("Team"));
        using var model = new CollectionsModel(state, h.Dialogs);

        model.Selected = "Default";
        Assert.Equal(["Other"], model.CopyTargets);   // not Default, which is the source, and not Team, which is synced
        model.Selected = "Other";
        Assert.Equal(["Default"], model.CopyTargets);
    }

    /// <summary>
    /// The picker's menu: every collection but the source, a synced one listed but disabled so it
    /// can say why, and CopyTargets exactly the enabled ones.
    /// </summary>
    [Fact]
    public void CopyDestinationsListEveryOtherCollectionAndDisableTheSyncedOnes()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Other"));
        Assert.Null(state.CreateCollection("Team"));
        Seed(h, state, File_(("Team", Synced("team.json"))));
        Assert.True(state.IsSynced("Team"));
        using var model = new CollectionsModel(state, h.Dialogs);

        model.Selected = "Default";
        // Not Default, which is the source; Team is there, but cannot take copies.
        Assert.Equal(
            [new CollectionsModel.CopyDestination("Other", true), new CollectionsModel.CopyDestination("Team", false)],
            model.CopyDestinations);
        Assert.Equal(model.CopyDestinations.Where(d => d.IsEnabled).Select(d => d.Name), model.CopyTargets);

        model.Selected = "Other";
        Assert.Equal(["Default", "Team"], model.CopyDestinations.Select(d => d.Name));   // the sidebar's order
        Assert.Equal([true, false], model.CopyDestinations.Select(d => d.IsEnabled));
        Assert.Equal(model.CopyDestinations.Where(d => d.IsEnabled).Select(d => d.Name), model.CopyTargets);
    }

    /// <summary>Remove needs something ticked.</summary>
    [Fact]
    public void TheRemovePredicateFollowsTheTicks()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.Upsert("alpha", Local("/bin/alpha"), null, "Default"));
        using var model = new CollectionsModel(state, h.Dialogs);

        model.Selected = "Default";
        Assert.False(model.CanRemoveChecked);   // nothing ticked yet
        model.SetChecked("alpha", true);
        Assert.True(model.CanRemoveChecked);
    }

    /// <summary>
    /// The kind guard specifically, not just an empty tick set: Selected clears the ticks on
    /// every switch, so a leg that ticks a row and only then switches to the synced collection
    /// would find the predicate false regardless of the IsSynced term — the empty tick set
    /// alone would explain it. Reload does not clear ticks, so ticking first and letting the
    /// *same* collection turn synced underneath is the one path that isolates the guard.
    /// </summary>
    [Fact]
    public void TheRemovePredicateIsGatedBySyncSpecifically()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        Assert.Null(state.Upsert("alpha", Local("/bin/alpha"), null, "Team"));
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Team";
        model.SetChecked("alpha", true);
        Assert.True(model.CanRemoveChecked);   // still local, and something is ticked

        Seed(h, state, File_(("Team", Synced("team.json"))));
        Assert.True(state.IsSynced("Team"));
        Assert.Equal(["alpha"], model.CheckedNames);   // reload does not clear the ticks
        Assert.False(model.CanRemoveChecked);   // the guard, not an empty tick set, is what changed
    }

    /// <summary>
    /// Removing the ticked rows asks first, names the connector when there is one and the count
    /// when there are more, and always says a copy remains in Backups.
    /// </summary>
    [Fact]
    public void RemoveCheckedConfirmsAndCarriesTheBackupsSentence()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        foreach (var name in new[] { "alpha", "beta" })
        {
            Assert.Null(state.Upsert(name, Local("/bin/" + name), null, "Default"));
        }
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Default";

        // Declined: nothing goes, and the earlier error does not come back.
        PresetError(model, h);
        model.SetChecked("alpha", true);
        h.Dialogs.NextConfirm = false;
        model.RemoveChecked();
        Assert.Null(model.LastError);   // a declined confirmation clears the last error
        Assert.True(state.Store.Collections["Default"].Mcps.ContainsKey("alpha"));    // declined, so alpha stays
        Assert.True(state.Store.Collections["Default"].Mcps.ContainsKey("beta"));
        Assert.Equal(CollectionsModel.RemoveCheckedInformative, h.Dialogs.Confirms[^1].Informative);
        Assert.True(h.Dialogs.Confirms[^1].Destructive);
        Assert.Contains("alpha", h.Dialogs.Confirms[^1].Message);   // one connector is named

        // Accepted, two ticked: the count is stated and the ticks are dropped.
        h.Dialogs.NextConfirm = true;
        model.SetChecked("beta", true);
        model.RemoveChecked();
        Assert.False(state.Store.Collections["Default"].Mcps.ContainsKey("alpha"));   // both ticked rows went
        Assert.False(state.Store.Collections["Default"].Mcps.ContainsKey("beta"));
        Assert.Contains("2", h.Dialogs.Confirms[^1].Message);   // several are counted
        Assert.Empty(model.CheckedNames);   // and the ticks go with them
        Assert.Null(model.LastError);   // a removal that lands clears the stale error
    }

    /// <summary>
    /// Remove(names, collection) persists but never applies on its own; the caller applies only
    /// when the collection losing rows is the active one — the same rule SetEnabled follows.
    /// </summary>
    [Fact]
    public void RemoveCheckedAppliesOnlyWhenTheActiveCollectionLosesRows()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Work"));
        Assert.Null(state.Upsert("gamma", Local("/bin/gamma"), null, "Work"));
        state.SwitchCollection("Default");
        using var model = new CollectionsModel(state, h.Dialogs);
        h.Dialogs.NextConfirm = true;

        // An enabled connector in the active collection that has not been applied yet: an apply
        // from the inactive leg below would write it, so that leg can catch an unconditional one.
        Assert.Null(state.Upsert("delta", Local("/bin/delta"), null, "Default"));
        Assert.True(state.Store.Collections["Default"].Mcps["delta"].Enabled);
        Assert.False(h.ClaudeServers().ContainsKey("delta"));   // upserted, not applied

        // Inactive collection: the write lands, but Claude's config is untouched.
        var before = h.ClaudeServers();
        model.Selected = "Work";
        model.SetChecked("gamma", true);
        model.RemoveChecked();
        Assert.False(state.Store.Collections["Work"].Mcps.ContainsKey("gamma"));   // removed from the store
        // Work was never active, so nothing Claude runs has changed.
        Assert.True(DictionaryEquality.Equal(before, h.ClaudeServers()));
        Assert.False(h.ClaudeServers().ContainsKey("delta"));   // no apply ran, so the pending connector is still unwritten

        // The active collection: the same call does apply.
        Assert.True(before.ContainsKey("aws-mcp"));   // there before, so its absence below is the apply's doing
        model.Selected = "Default";
        model.SetChecked("aws-mcp", true);
        model.RemoveChecked();
        Assert.False(state.Store.Collections["Default"].Mcps.ContainsKey("aws-mcp"));
        Assert.False(h.ClaudeServers().ContainsKey("aws-mcp"));   // the active collection changed, so Claude's config follows
    }

    /// <summary>
    /// The copy lands in the target, disabled, and the ticks go with it — the same clearing
    /// RemoveChecked does on success. Every copy arrives disabled, so this never applies.
    /// </summary>
    [Fact]
    public void CopyCheckedCopiesIntoTheTargetClearsTicksAndDoesNotApply()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        // Created first: CreateCollection starts a collection as a copy of whichever one is
        // active when it is made, so making Spare before alpha exists keeps alpha out of that
        // starting snapshot — otherwise the copy below would collide with it and land as
        // "alpha 2" instead, leaving the original untouched and this test green for the wrong
        // reason.
        Assert.Null(state.CreateCollection("Spare"));
        Assert.Null(state.Upsert("alpha", Local("/bin/alpha"), null, "Default"));
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Default";
        model.SetChecked("alpha", true);

        PresetError(model, h);
        var before = h.ClaudeServers();
        Assert.True(model.CopyChecked("Spare"));
        Assert.False(state.Store.Collections["Spare"].Mcps["alpha"].Enabled);   // the copy landed, disabled
        Assert.Empty(model.CheckedNames);   // the ticks went with it
        Assert.Null(model.LastError);   // a copy that lands clears the stale error
        // Every copy arrives disabled, so nothing Claude runs has changed.
        Assert.True(DictionaryEquality.Equal(before, h.ClaudeServers()));
    }

    /// <summary>
    /// The two early-outs CopyChecked documents: a target outside CopyTargets — the source
    /// itself, or a synced collection — and an empty tick set. Both return false without reaching
    /// AppState.MakeLocalCopy, so nothing lands anywhere and the ticks are left standing. The last
    /// error is cleared, so the window does not show it again as if this copy had failed.
    /// </summary>
    [Fact]
    public void CopyCheckedRefusesATargetOutsideCopyTargets()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        // Created first, for the same reason as the success test above, and so seeding the
        // sidecar afterwards has something in the master list to annotate (CollectionsFile.Reconciled).
        Assert.Null(state.CreateCollection("Team"));
        Assert.Null(state.Upsert("alpha", Local("/bin/alpha"), null, "Default"));
        Seed(h, state, File_(("Team", Synced("team.json"))));
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Default";
        model.SetChecked("alpha", true);
        PresetError(model, h);

        Assert.False(model.CopyChecked("Default"));   // the source is not a target
        Assert.False(model.CopyChecked("Team"));      // a synced collection is not a target either
        Assert.Equal(["alpha"], model.CheckedNames);   // both are unreachable early-outs, so the ticks stand
        Assert.Null(model.LastError);   // the earlier error is not shown again
        Assert.False(state.Store.Collections["Team"].Mcps.ContainsKey("alpha"));   // nothing landed in Team
    }

    [Fact]
    public void CopyCheckedRefusesWhenNothingIsTicked()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Spare"));
        var before = state.Store.Collections["Spare"];
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Default";

        Assert.False(model.CopyChecked("Spare"));   // nothing ticked, so there is nothing to copy
        Assert.Equal(before, state.Store.Collections["Spare"]);   // and nothing about Spare changed
    }

    /// <summary>
    /// Copy to ▸ New Collection: an empty collection holding only the copies, while the window
    /// stays where the rows came from and nothing Claude runs changes.
    /// </summary>
    [Fact]
    public void CopyCheckedIntoNewCollectionMakesAnEmptyCollectionOfTheCopiesAlone()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.Upsert("alpha", Local("/bin/alpha"), null, "Default"));
        Assert.Null(state.Upsert("beta", Local("/bin/beta"), null, "Default"));
        Assert.True(state.Store.Collections["Default"].Mcps.ContainsKey("aws-mcp"));
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Default";
        model.SetChecked("alpha", true);
        model.SetChecked("beta", true);
        PresetError(model, h);

        var before = h.ClaudeServers();
        h.Dialogs.NextPromptAnswer = "  Fresh  ";
        Assert.True(model.CopyCheckedIntoNewCollection());
        Assert.Equal(new FakeDialogs.PromptCall(AppState.NewCollectionTitle, ""), h.Dialogs.Prompts[^1]);
        var fresh = state.Store.Collections["Fresh"];   // the store trimmed the name
        // The copies alone, not a copy of the active collection.
        Assert.Equal(["alpha", "beta"], fresh.Mcps.Keys.Order(StringComparer.Ordinal));
        Assert.False(fresh.Mcps.ContainsKey("aws-mcp"));
        Assert.All(fresh.Mcps.Values, entry => Assert.False(entry.Enabled));   // they arrive disabled
        Assert.Equal("Default", model.Selected);   // the window stays where the rows came from
        Assert.Equal("Default", state.ActiveCollection);
        Assert.Empty(model.CheckedNames);   // the ticks went with them
        Assert.Null(model.LastError);
        Assert.True(DictionaryEquality.Equal(before, h.ClaudeServers()));   // nothing Claude runs has changed
    }

    [Fact]
    public void CopyCheckedIntoNewCollectionCancelledChangesNothing()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.Upsert("alpha", Local("/bin/alpha"), null, "Default"));
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Default";
        model.SetChecked("alpha", true);

        h.Dialogs.NextPromptAnswer = null;
        Assert.False(model.CopyCheckedIntoNewCollection());
        Assert.Equal(["Default"], state.CollectionNames);
        Assert.Equal(["alpha"], model.CheckedNames);
        Assert.Null(model.LastError);
    }

    /// <summary>
    /// A name the store refuses is the store's error, and nothing is made or copied. The ticks
    /// stay for a retry, as they do after any copy that did not land.
    /// </summary>
    [Fact]
    public void CopyCheckedIntoNewCollectionReportsARefusedName()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Work"));
        Assert.Null(state.Upsert("alpha", Local("/bin/alpha"), null, "Default"));
        state.SwitchCollection("Default");
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Default";
        model.SetChecked("alpha", true);

        h.Dialogs.NextPromptAnswer = "Work";
        Assert.False(model.CopyCheckedIntoNewCollection());
        Assert.Equal("A collection named “Work” already exists.", model.LastError);   // the store's own words
        Assert.Equal(["Default", "Work"], state.CollectionNames);
        Assert.False(state.Store.Collections["Work"].Mcps.ContainsKey("alpha"));   // nothing landed in the collection of that name
        Assert.Equal("Default", model.Selected);
        Assert.Equal(["alpha"], model.CheckedNames);
    }

    /// <summary>The ticked names a destination already holds, sorted, and nothing when none clash.</summary>
    [Fact]
    public void CheckedNamesClashingNamesTheTickedConnectorsTheDestinationHolds()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Spare"));
        state.SwitchCollection("Default");
        foreach (var name in new[] { "zeta", "alpha", "beta" })
        {
            Assert.Null(state.Upsert(name, Local("/bin/" + name), null, "Default"));
        }
        foreach (var name in new[] { "zeta", "alpha" })
        {
            Assert.Null(state.Upsert(name, Local("/bin/other"), null, "Spare"));
        }
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Default";
        model.SetChecked("zeta", true);
        model.SetChecked("beta", true);
        model.SetChecked("alpha", true);

        Assert.Equal(["alpha", "zeta"], model.CheckedNamesClashing("Spare"));   // beta is not in Spare
        model.SetChecked("alpha", false);
        model.SetChecked("zeta", false);
        Assert.Empty(model.CheckedNamesClashing("Spare"));   // nothing ticked clashes
    }

    // MARK: header pills and menu

    [Fact]
    public void PillsMarkActivePublishedAndSubscribedExceptions()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Shared"));
        Assert.Null(state.CreateCollection("Team"));
        Assert.Null(state.CreateCollection("Plain"));
        state.SwitchCollection("Default");
        var file = File_(("Shared", Published("shared")), ("Team", Synced("team.json")));
        var cache = Cache([new("Team", Bound("/shared/team.json"))],
                          [new("Shared", new CollectionsLocalCache.PublishBinding("/tmp/share", null))]);
        Seed(h, state, file, cache);

        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Default";
        Assert.Equal([CollectionsModel.Pill.Active], model.Pills);

        model.Selected = "Shared";
        Assert.Equal([CollectionsModel.Pill.Published], model.Pills);

        state.SwitchCollection("Team");
        model.Selected = "Team";
        Assert.Equal([CollectionsModel.Pill.Active, CollectionsModel.Pill.Subscribed], model.Pills);

        model.Selected = "Plain";
        Assert.Empty(model.Pills);   // an ordinary local collection, not active, carries no pill
    }

    [Fact]
    public void MenuAndPillTitlesNameTheirStrings()
    {
        Assert.Equal(CollectionsModel.ActivePill, CollectionsModel.Title(CollectionsModel.Pill.Active));
        Assert.Equal(CollectionsModel.PublishedPill, CollectionsModel.Title(CollectionsModel.Pill.Published));
        Assert.Equal(CollectionsModel.SubscribedPill, CollectionsModel.Title(CollectionsModel.Pill.Subscribed));

        Assert.Equal(CollectionsModel.MakeActiveAction, CollectionsModel.Title(new CollectionsModel.MenuEntry.MakeActive()));
        Assert.Equal(CollectionsModel.RenameAction, CollectionsModel.Title(new CollectionsModel.MenuEntry.Rename()));
        Assert.Equal(CollectionsModel.DuplicateAction, CollectionsModel.Title(new CollectionsModel.MenuEntry.Duplicate()));
        Assert.Equal(CollectionsModel.PublishButton, CollectionsModel.Title(new CollectionsModel.MenuEntry.StartPublishing()));
        Assert.Equal(CollectionsModel.PublishSettingsButton, CollectionsModel.Title(new CollectionsModel.MenuEntry.PublishingSettings()));
        Assert.Equal(CollectionsModel.StopPublishingAction, CollectionsModel.Title(new CollectionsModel.MenuEntry.StopPublishing()));
        Assert.Equal(CollectionsModel.ShowPublishedFileAction, CollectionsModel.Title(new CollectionsModel.MenuEntry.ShowPublishedFile()));
        Assert.Equal(CollectionsModel.ExportAllAction, CollectionsModel.Title(new CollectionsModel.MenuEntry.ExportAll(true)));
        Assert.Equal(CollectionsModel.ExportAllAction, CollectionsModel.Title(new CollectionsModel.MenuEntry.ExportAll(false)));
        Assert.Equal(CollectionsModel.MakeLocalCopyButton, CollectionsModel.Title(new CollectionsModel.MenuEntry.MakeLocalCopy()));
        Assert.Equal(CollectionsModel.RefreshButton, CollectionsModel.Title(new CollectionsModel.MenuEntry.Refresh()));
        Assert.Equal(CollectionsModel.ShowSourceFileAction, CollectionsModel.Title(new CollectionsModel.MenuEntry.ShowSourceFile()));
        Assert.Equal(CollectionsModel.StopSyncingAction, CollectionsModel.Title(new CollectionsModel.MenuEntry.StopSyncing()));
        Assert.Equal(CollectionsModel.DeleteAction, CollectionsModel.Title(new CollectionsModel.MenuEntry.Delete(true)));
        Assert.Equal(CollectionsModel.DeleteAction, CollectionsModel.Title(new CollectionsModel.MenuEntry.Delete(false)));
        Assert.Equal("", CollectionsModel.Title(new CollectionsModel.MenuEntry.Separator()));
    }

    [Fact]
    public void CollectionMenuForLocalActiveUnpublished()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        // A second local collection, so the common case is not also the last-collection case.
        Assert.Null(state.CreateCollection("Work"));
        state.SwitchCollection("Default");
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Default";
        Assert.Equal(
            [
                new CollectionsModel.MenuEntry.Rename(), new CollectionsModel.MenuEntry.Duplicate(), new CollectionsModel.MenuEntry.Separator(),
                new CollectionsModel.MenuEntry.StartPublishing(), new CollectionsModel.MenuEntry.ExportAll(true), new CollectionsModel.MenuEntry.Separator(),
                new CollectionsModel.MenuEntry.Delete(true),
            ],
            model.CollectionMenu);
    }

    [Fact]
    public void CollectionMenuForLocalPublishedNotActive()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        var folder = h.Dir.File("share");
        Directory.CreateDirectory(folder);
        Assert.Null(state.CreateCollection("Shared"));
        Assert.Null(state.StartPublishing("Shared", folder, PublishIntent.None));
        state.SwitchCollection("Default");   // Shared stays, now not the active collection

        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Shared";
        Assert.Equal(
            [
                new CollectionsModel.MenuEntry.MakeActive(), new CollectionsModel.MenuEntry.Separator(),
                new CollectionsModel.MenuEntry.Rename(), new CollectionsModel.MenuEntry.Duplicate(), new CollectionsModel.MenuEntry.Separator(),
                new CollectionsModel.MenuEntry.PublishingSettings(), new CollectionsModel.MenuEntry.StopPublishing(),
                new CollectionsModel.MenuEntry.ShowPublishedFile(), new CollectionsModel.MenuEntry.ExportAll(true), new CollectionsModel.MenuEntry.Separator(),
                new CollectionsModel.MenuEntry.Delete(true),
            ],
            model.CollectionMenu);
    }

    [Fact]
    public void CollectionMenuForSyncedNotActiveLocated()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        state.SwitchCollection("Default");
        Seed(h, state, File_(("Team", Synced("team.json"))));
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Team";

        // Located via a real file, the same flow the banner strip's Locate button drives.
        var document = h.Dir.File("team.json");
        System.IO.File.WriteAllBytes(document, CollectionDocumentSamples.DataTeam.Serialize());
        Assert.Null(model.LocateSource(document));
        Assert.True(state.IsLocated("Team"));

        Assert.Equal(
            [
                new CollectionsModel.MenuEntry.MakeActive(), new CollectionsModel.MenuEntry.Separator(),
                new CollectionsModel.MenuEntry.Rename(), new CollectionsModel.MenuEntry.MakeLocalCopy(), new CollectionsModel.MenuEntry.Separator(),
                new CollectionsModel.MenuEntry.Refresh(), new CollectionsModel.MenuEntry.ShowSourceFile(), new CollectionsModel.MenuEntry.StopSyncing(),
                new CollectionsModel.MenuEntry.ExportAll(false), new CollectionsModel.MenuEntry.Separator(),
                new CollectionsModel.MenuEntry.Delete(true),
            ],
            model.CollectionMenu);
    }

    /// <summary>
    /// A synced collection whose document has never been found on this machine: nothing to
    /// refresh and nothing to reveal, so both menu rows drop out together.
    /// </summary>
    [Fact]
    public void CollectionMenuOmitsRefreshAndShowSourceFileWhenNotLocated()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        state.SwitchCollection("Default");
        Seed(h, state, File_(("Team", Synced("team.json"))));
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Team";

        Assert.False(state.IsLocated("Team"));
        Assert.Null(model.SourceFilePath);
        Assert.False(model.CanRefresh);
        Assert.DoesNotContain(model.CollectionMenu, e => e is CollectionsModel.MenuEntry.Refresh);
        Assert.DoesNotContain(model.CollectionMenu, e => e is CollectionsModel.MenuEntry.ShowSourceFile);
        Assert.Contains(model.CollectionMenu, e => e is CollectionsModel.MenuEntry.StopSyncing);   // still offered: nothing is lost by stopping
    }

    [Fact]
    public void CollectionMenuDisablesDeleteForTheLastLocalCollection()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        using var model = new CollectionsModel(state, h.Dialogs);
        // Only "Default" exists.
        Assert.False(model.CanDelete);
        Assert.Contains(model.CollectionMenu, e => e is CollectionsModel.MenuEntry.Delete { Enabled: false });
    }

    [Fact]
    public void DuplicateCopiesConnectorsDisabledWithoutActivatingAndReportsAClash()
    {
        // Not seeded: CreateCollection copies whichever collection is active when it runs, and
        // Default is still active at that point.
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        Assert.Null(state.Upsert("alpha", Local("/bin/alpha"), null, "Team"));
        Assert.Null(state.Upsert("beta", Local("/bin/beta"), null, "Team"));
        state.SwitchCollection("Default");
        using var model = new CollectionsModel(state, h.Dialogs);
        model.Selected = "Team";

        // Cancelled: nothing changes.
        h.Dialogs.NextPromptAnswer = null;
        Assert.False(model.Duplicate());
        Assert.Equal(["Default", "Team"], state.CollectionNames);

        var before = h.ClaudeServers();
        h.Dialogs.NextPromptAnswer = "  Team Copy  ";
        Assert.True(model.Duplicate());
        Assert.Equal(new FakeDialogs.PromptCall(AppState.NewCollectionTitle, ""), h.Dialogs.Prompts[^1]);
        var copy = state.Store.Collections["Team Copy"];   // the store trimmed the name
        Assert.Equal(["alpha", "beta"], copy.Mcps.Keys.Order(StringComparer.Ordinal));
        Assert.Equal([false, false], copy.Mcps.Values.Select(v => v.Enabled));   // every copy arrives disabled
        Assert.Equal("Default", state.ActiveCollection);   // duplicate does not activate
        Assert.True(DictionaryEquality.Equal(before, h.ClaudeServers()));   // nothing Claude runs has changed
        Assert.Equal("Team", model.Selected);   // the selection stays where it was
        Assert.Null(model.LastError);

        // A name the store refuses is the model's error.
        h.Dialogs.NextPromptAnswer = "Team Copy";
        Assert.False(model.Duplicate());
        Assert.NotNull(model.LastError);
    }

    [Fact]
    public void PublishedFilePathAndSourceFilePathNameTheDocumentsThisMachineKnows()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Shared"));
        Assert.Null(state.CreateCollection("Team"));
        state.SwitchCollection("Default");
        Seed(h, state, File_(("Team", Synced("team.json"))));

        var folder = h.Dir.File("share");
        Directory.CreateDirectory(folder);
        Assert.Null(state.StartPublishing("Shared", folder, PublishIntent.None));

        using var model = new CollectionsModel(state, h.Dialogs);

        model.Selected = "Shared";
        var published = model.PublishedFilePath;
        Assert.NotNull(published);
        Assert.EndsWith("shared." + CollectionDocument.FileExtension, published);

        model.Selected = "Default";
        Assert.Null(model.PublishedFilePath);   // not published from here

        model.Selected = "Team";
        Assert.Null(model.SourceFilePath);   // not located yet
        var document = h.Dir.File("team.json");
        System.IO.File.WriteAllBytes(document, CollectionDocumentSamples.DataTeam.Serialize());
        Assert.Null(model.LocateSource(document));
        Assert.Equal(state.SourceLocation("Team"), model.SourceFilePath);
        Assert.Equal(document, model.SourceFilePath);
    }

    // MARK: sidebar and connectors header

    [Fact]
    public void AddConnectorAffordanceFollowsSyncAndTargetsTheSelectedCollection()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        state.SwitchCollection("Default");
        Seed(h, state, File_(("Team", Synced("team.json"))));
        using var model = new CollectionsModel(state, h.Dialogs);

        model.Selected = "Default";
        Assert.True(model.CanAddConnector);
        Assert.Equal(CollectionsModel.AddConnectorTooltip, model.AddConnectorTooltipText);
        var target = model.NewConnectorTarget();
        Assert.Equal("Default", target.Collection);
        Assert.True(target.IsNew);

        model.Selected = "Team";
        Assert.False(model.CanAddConnector);
        Assert.Equal(CollectionsModel.AddConnectorDisabledTooltip, model.AddConnectorTooltipText);
        Assert.Equal("Team", model.NewConnectorTarget().Collection);
    }
}

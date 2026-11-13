using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>Mirror: Tests/ConnectorControlStateTests/ControlToolsReadTests.swift</summary>
public class ControlToolsReadTests
{
    private const string Token = "ghp_0123456789abcdefghijklmnopqrstuvwxyzAB";

    /// <summary>The token in its environment and, after a flag, in its arguments, where a summary of what it
    /// runs could show it.</summary>
    private static McpEntry Github => new(JsonValue.Object(
        ("command", JsonValue.String("npx")),
        ("args", JsonValue.Array([JsonValue.String("-y"), JsonValue.String("@modelcontextprotocol/server-github"),
                                  JsonValue.String("--token"), JsonValue.String(Token)])),
        ("env", JsonValue.Object(("GITHUB_PERSONAL_ACCESS_TOKEN", JsonValue.String(Token))))));

    private static JsonValue? At(JsonValue value, params string[] segments) => value.ValueAt(new JsonPointer(segments));

    private static JsonValue Args(params (string Key, JsonValue Value)[] arguments) => JsonValue.Object(arguments);

    [Fact]
    public void StatusSaysWhatClaudeRunsAndWhetherItNeedsARestart()
    {
        using var rig = new ControlRig();
        rig.H.ClaudeRunningSince(hours: 1);
        rig.State.RefreshRestartState();
        var result = rig.Call("get_status");
        Assert.NotNull(result);
        Assert.Equal(string.Join("\n",
            "Connector Control 1.2.2",
            "Active collection    Default · 3 connectors, 3 on",
            "Claude               running · restart required",
            "Claude integration   on · confirmations on",
            $"Master list          {rig.State.Service.Paths.MasterStorePath}"), result.Text);
        Assert.False(result.IsError);
        Assert.Equal(JsonValue.Bool(true), At(result.Structured, "claude", "restartRequired"));
        Assert.Equal(JsonValue.Int(3), At(result.Structured, "connectors"));
        Assert.Equal(JsonValue.Bool(true), At(result.Structured, "integration", "enabled"));
        Assert.Equal(JsonValue.Null, At(result.Structured, "problem"));
    }

    [Fact]
    public void CheckToolsReportsEachLauncher()
    {
        using var rig = new ControlRig();
        rig.H.Tools.Statuses[Tool.Uvx] = ToolStatus.NotFound;
        var result = rig.Call("check_tools");
        Assert.NotNull(result);
        Assert.Equal(string.Join("\n",
            "npx    Found       /fake/bin/npx",
            "node   Found       /fake/bin/node",
            "uvx    Not found   " + ControlTools.InstallHint(ToolInfo.LinkTitle(ToolFamily.Uv), ToolInfo.InstallCommand(ToolFamily.Uv)),
            "uv     Found       /fake/bin/uv"), result.Text);
        Assert.Equal(JsonValue.String("notFound"), At(result.Structured, "tools", "2", "status"));
        Assert.Equal(JsonValue.String("/fake/bin/npx"), At(result.Structured, "tools", "0", "path"));
    }

    [Fact]
    public void WaitingOnProbesRunsAtOnceWhenNoneIsInFlight()
    {
        using var rig = new ControlRig();
        var ran = false;
        rig.State.WhenToolsProbed(null, () => ran = true);
        Assert.True(ran);
    }

    [Fact]
    public void ListCollectionsMarksTheActiveOne()
    {
        using var rig = new ControlRig();
        Assert.Null(rig.State.AddEmptyCollection("Work"));
        var result = rig.Call("list_collections");
        Assert.NotNull(result);
        Assert.Equal(string.Join("\n",
            "         NAME      KIND    CONNECTORS",
            "active   Default   local   3 connectors, 3 on",
            "         Work      local   0 connectors, 0 on"), result.Text);
        Assert.Equal(JsonValue.String("Default"), At(result.Structured, "active"));
        Assert.Equal(JsonValue.Int(0), At(result.Structured, "collections", "1", "connectors"));
    }

    [Fact]
    public void ListConnectorsShowsWhatEachRunsAndNeverASecret()
    {
        using var rig = new ControlRig();
        Assert.Null(rig.State.Upsert("github", Github, null));
        var result = rig.Call("list_connectors");
        Assert.NotNull(result);
        Assert.Equal(string.Join("\n",
            "     NAME          RUNS",
            "on   aws-mcp       aws-mcp.us-east-1.api.aws",
            $"on   github        {ConnectorTarget.TargetOf(Github.Config)}",
            "on   scoutbook     scoutbook.example.com",
            "on   service-now   snow.example.com"), result.Text);
        Assert.DoesNotContain("ghp_", result.Text, StringComparison.Ordinal);
        Assert.Equal(ControlResult.Refusal(MasterStore.NoCollectionError("Nope")),
            rig.Call("list_connectors", Args(("collection", JsonValue.String("Nope")))));
    }

    [Fact]
    public void GetConnectorMasksSecretsAndOnlyTheCommandLineMayReveal()
    {
        using var rig = new ControlRig();
        Assert.Null(rig.State.Upsert("github", Github, null));
        var shown = rig.Call("get_connector", Args(("name", JsonValue.String("github"))));
        Assert.NotNull(shown);
        Assert.Equal(string.Join("\n", new[] { "github · Default · on" }.Concat(ControlMasking.CodeLines(Github.Config))), shown.Text);
        Assert.Equal(JsonValue.String("set"), At(shown.Structured, "config", "env", "GITHUB_PERSONAL_ACCESS_TOKEN"));
        var asked = rig.Call("get_connector", Args(("name", JsonValue.String("github")), ("reveal", JsonValue.Bool(true))));
        Assert.NotNull(asked);
        // Claude never sees a secret, whatever it asks for.
        Assert.DoesNotContain(Token, asked.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, asked.Structured.CompactLine(), StringComparison.Ordinal);
        var revealed = rig.Call("get_connector", Args(("name", JsonValue.String("github")), ("reveal", JsonValue.Bool(true))),
            new ControlCaller.Cli());
        Assert.NotNull(revealed);
        Assert.Contains(Token, revealed.Text, StringComparison.Ordinal);
        Assert.Equal(ControlResult.Refusal(ControlTools.NoConnectorError("nope", "Default")),
            rig.Call("get_connector", Args(("name", JsonValue.String("nope")))));
    }

    [Fact]
    public void PendingUpdateRefusesALocalCollectionAndDescribesASubscribedOne()
    {
        using var rig = new ControlRig();
        Assert.Equal(ControlResult.Refusal(ControlTools.NotSubscribedError("Default")),
            rig.Call("get_pending_update", Args(("collection", JsonValue.String("Default")))));
        var path = rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam);
        Assert.Equal(ControlTools.UpToDateText("Data team"),
            rig.Call("get_pending_update", Args(("collection", JsonValue.String("Data team"))))?.Text);
        var sample = CollectionDocumentSamples.DataTeam;
        var connectors = new Dictionary<string, CollectionDocument.Connector>(sample.Connectors, StringComparer.Ordinal);
        connectors.Remove("github");
        AppStateHarness.WriteDocumentAt(new CollectionDocument(sample.Name, sample.Author, sample.Origin, sample.Exported, connectors), path);
        rig.State.RefreshSource("Data team");
        var pending = rig.Call("get_pending_update", Args(("collection", JsonValue.String("Data team"))));
        Assert.NotNull(pending);
        Assert.Equal(ControlTools.PendingUpdateText("Data team", ControlTools.DeletesClause("“github”")), pending.Text);
        Assert.Equal(JsonValue.Array([JsonValue.String("github")]), At(pending.Structured, "removed"));
    }

    /// <summary>The backups the Restore sheet lists, as it finds them. Platform idiom: the Mac's
    /// BackupManager names the first-run original's URL; here, as the Restore sheet does, it is
    /// looked for beside the others.</summary>
    private static List<string> RestoreList(ControlRig rig)
    {
        var found = new List<string>(rig.State.Service.Backups.Backups(RestoreModel.Series));
        var original = Path.Combine(rig.State.Service.Backups.BackupsDir, $"{RestoreModel.Series}.original.json");
        if (File.Exists(original))
        {
            found.Add(original);
        }
        return found;
    }

    [Fact]
    public void ListBackupsGivesEachIdTheRestoreTakes()
    {
        using var rig = new ControlRig();
        var expected = RestoreList(rig);
        // The first load's apply backed Claude's config up.
        Assert.NotEmpty(expected);
        var result = rig.Call("list_backups");
        Assert.NotNull(result);
        var items = At(result.Structured, "backups");
        Assert.NotNull(items);
        Assert.Equal(expected.Select(path => JsonValue.String(Path.GetFileName(path))), items.ArrayItems.Select(item => At(item, "id")));
        Assert.StartsWith(ControlTools.ClaudeBackupsHeading + "\nID ", result.Text, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(expected[0]), result.Text, StringComparison.Ordinal);
    }

    /// <summary>The backups folder emptied, then holding only <paramref name="files"/>, each as its text: what
    /// <c>list_backups</c> lists is then exactly what a test put there.</summary>
    private static void Backups(ControlRig rig, params (string Name, string Text)[] files)
    {
        Directory.CreateDirectory(rig.H.BackupsDir);
        foreach (var path in Directory.GetFiles(rig.H.BackupsDir))
        {
            File.Delete(path);
        }
        foreach (var (name, text) in files)
        {
            File.WriteAllText(Path.Combine(rig.H.BackupsDir, name), text);
        }
    }

    private const string ClaudeBackup = "claude_desktop_config.2026-11-01T00-00-00-000Z.json";
    private const string NewerBackup = "mcps.2026-11-02T00-00-00-000Z.json";
    private const string OlderBackup = "mcps.2026-11-01T00-00-00-000Z.json";

    /// <summary>A master list as a backup holds it: Personal, with one connector, on, and Default, with two,
    /// one of them on.</summary>
    private const string MasterList = """
        {"version": 2, "activeProfile": "Default", "profiles": {
          "Personal": {"mcps": {"notes": {"enabled": true, "config": {"command": "node"}, "lastEditView": "form"}}},
          "Default": {"mcps": {"github": {"enabled": true, "config": {"command": "node"}, "lastEditView": "form"},
                               "splunk": {"enabled": false, "config": {"command": "node"}, "lastEditView": "form"}}}}}
        """;

    /// <summary>Both kinds, each under its heading: Claude's config's as before, then a row for each collection
    /// a master-list backup holds, by name, newest backup first; one that cannot be read says so.</summary>
    [Fact]
    public void ListBackupsListsBothKindsUnderTheirHeadings()
    {
        using var rig = new ControlRig();
        Backups(rig, (ClaudeBackup, """{"mcpServers": {}}"""), (NewerBackup, MasterList), (OlderBackup, "{"));
        BackupCollections.Record("Default", Path.Combine(rig.H.BackupsDir, ClaudeBackup), rig.H.BackupsDir);
        var result = rig.Call("list_backups");
        Assert.NotNull(result);
        Assert.Equal(string.Join("\n",
            "Claude’s config:",
            "ID                                                    COLLECTION",
            "claude_desktop_config.2026-11-01T00-00-00-000Z.json   Default",
            "",
            "Collections:",
            "ID                                   COLLECTION   CONNECTORS",
            "mcps.2026-11-02T00-00-00-000Z.json   Default      2 connectors, 1 on",
            "mcps.2026-11-02T00-00-00-000Z.json   Personal     1 connector, 1 on",
            "mcps.2026-11-01T00-00-00-000Z.json                can’t be read"), result.Text);
        Assert.Equal(JsonValue.Object(
            ("backups", JsonValue.Array([JsonValue.Object(("id", JsonValue.String(ClaudeBackup)), ("collection", JsonValue.String("Default")))])),
            ("collectionBackups", JsonValue.Array([
                JsonValue.Object(("id", JsonValue.String(NewerBackup)), ("collections", JsonValue.Array([
                    JsonValue.Object(("name", JsonValue.String("Default")), ("connectors", JsonValue.Int(2)), ("on", JsonValue.Int(1))),
                    JsonValue.Object(("name", JsonValue.String("Personal")), ("connectors", JsonValue.Int(1)), ("on", JsonValue.Int(1))),
                ]))),
                JsonValue.Object(("id", JsonValue.String(OlderBackup)), ("collections", JsonValue.Null)),
            ]))), result.Structured);
    }

    /// <summary>Only the kind there is, under its heading and with no blank line before it; neither kind is the
    /// sentence it always was.</summary>
    [Fact]
    public void ListBackupsListsOnlyTheKindThereIsAndSaysWhenThereAreNone()
    {
        using var rig = new ControlRig();
        Backups(rig, (NewerBackup, MasterList));
        var collections = rig.Call("list_backups");
        Assert.NotNull(collections);
        Assert.StartsWith("Collections:\nID ", collections.Text, StringComparison.Ordinal);
        Assert.Equal(JsonValue.Array([]), At(collections.Structured, "backups"));
        Backups(rig, (ClaudeBackup, """{"mcpServers": {}}"""));
        var claude = rig.Call("list_backups");
        Assert.NotNull(claude);
        Assert.Equal(string.Join("\n", "Claude’s config:", "ID                                                    COLLECTION", ClaudeBackup), claude.Text);
        Assert.Equal(JsonValue.Array([]), At(claude.Structured, "collectionBackups"));
        Backups(rig);
        Assert.Equal(new ControlResult(JsonValue.Object(("backups", JsonValue.Array([])), ("collectionBackups", JsonValue.Array([]))),
            ControlTools.NoBackupsText, false), rig.Call("list_backups"));
    }

    [Fact]
    public void SettingsListsEveryKeySetSettingTakes()
    {
        using var rig = new ControlRig();
        Assert.Equal(string.Join("\n",
            "launch-at-login           off",
            "confirm-restart           on",
            "confirm-quit              on",
            "notify-external-changes   on",
            "automatic-updates         off",
            "backups-to-keep           20",
            "claude-integration        on",
            "confirm-claude-changes    on"), rig.Call("get_settings")?.Text);
    }

    /// <summary>Windows only: the Mac has no install to detect. Reading the settings reads launch at login fresh
    /// but never walks Claude's packages, which only the Claude tab shows.</summary>
    [Fact]
    public void ReadingTheSettingsNeverDetectsClaudesInstall()
    {
        using var rig = new ControlRig();
        rig.Call("get_settings");
        Assert.Equal(0, rig.Install.DetectCalls);
    }

    /// <summary>Arguments for the read tools that need some: each names something that is there, so every
    /// read answers in full.</summary>
    private static readonly Dictionary<string, JsonValue> ReadArguments = new(StringComparer.Ordinal)
    {
        ["get_connector"] = Args(("name", JsonValue.String("github"))),
        ["get_pending_update"] = Args(("collection", JsonValue.String("Data team"))),
    };

    [Fact]
    public void EveryReadToolLeavesTheStoreTheSettingsAndClaudesConfigByteIdentical()
    {
        using var rig = new ControlRig();
        Assert.Null(rig.State.Upsert("github", Github, null));
        rig.State.ApplyInteractively();
        // A subscribed collection with an update waiting, so the pending-update read describes one.
        var path = rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam);
        var sample = CollectionDocumentSamples.DataTeam;
        var connectors = new Dictionary<string, CollectionDocument.Connector>(sample.Connectors, StringComparer.Ordinal);
        connectors.Remove("github");
        AppStateHarness.WriteDocumentAt(new CollectionDocument(sample.Name, sample.Author, sample.Origin, sample.Exported, connectors), path);
        rig.State.RefreshSource("Data team");
        rig.H.Drain();
        Assert.True(rig.State.PendingUpdates.ContainsKey("Data team"));
        string[] files = [rig.H.MasterStorePath, Path.Combine(rig.H.StoreDir, CollectionsFile.FileName),
                          rig.H.ClaudeConfigPath, rig.State.Service.Paths.CollectionsCachePath];
        List<byte[]?> Bytes() => files.Select(file => File.Exists(file) ? File.ReadAllBytes(file) : null).ToList();
        // Platform idiom: each side lists its own settings seam, which differ - the Mac keeps Claude's
        // app path, Windows its config path, launch target, tray tip and declined update.
        List<string> Settings()
        {
            var s = rig.H.Settings;
            return [s.MasterStoreDir ?? "", s.ClaudeConfigPath ?? "", s.ClaudeLaunchTarget ?? "", $"{s.BackupKeepCount}",
                    $"{s.NotifyExternalChanges}", $"{s.ConfirmBeforeRestart}", $"{s.ConfirmBeforeQuit}", $"{s.LastApplyDate:O}",
                    $"{s.SweepVersion}", $"{s.ClaudeIntegrationEnabled}", $"{s.ConfirmClaudeChanges}",
                    $"{s.ClaudeIntegrationOfferAnswered}", $"{rig.Autostart.Enabled}", $"{s.AutoUpdate}",
                    $"{s.TrayTipShown}", s.DeclinedUpdateVersion ?? ""];
        }
        var filesBefore = Bytes();
        var settingsBefore = Settings();
        var backupsBefore = rig.State.Service.Backups.Backups(RestoreModel.Series);
        var sentBefore = rig.H.Notifier.Sent.Count;
        foreach (var tool in ControlTools.Catalog.Where(t => t.Gate == ControlGateClass.Read))
        {
            Assert.True(rig.Call(tool.Name, ReadArguments.GetValueOrDefault(tool.Name))?.IsError == false, tool.Name);
        }
        var filesAfter = Bytes();
        Assert.Equal(filesBefore.Count, filesAfter.Count);
        for (var index = 0; index < files.Length; index++)
        {
            Assert.True(filesBefore[index] is null ? filesAfter[index] is null
                : filesAfter[index] is { } after && filesBefore[index]!.AsSpan().SequenceEqual(after), files[index]);
        }
        Assert.Equal(settingsBefore, Settings());
        Assert.Equal(backupsBefore, rig.State.Service.Backups.Backups(RestoreModel.Series));
        Assert.Equal(sentBefore, rig.H.Notifier.Sent.Count);
    }

    /// <summary>The names a read tool echoes in a sentence reach the command line's terminal, so they show as every
    /// other sentence shows a name: no control character goes out as itself, and a backslash is doubled.</summary>
    [Fact]
    public void TheReadToolsShowTheNamesTheyEchoOneWayOnly()
    {
        using var rig = new ControlRig();
        var name = "e\e[31m\\x";
        var shown = """e\u{001B}[31m\\x""";
        Assert.Equal(ControlResult.Refusal($"No collection named “{shown}”."),
            rig.Call("list_connectors", Args(("collection", JsonValue.String(name)))));
        Assert.Equal(ControlResult.Refusal($"No collection named “{shown}”."),
            rig.Call("get_connector", Args(("name", JsonValue.String("x")), ("collection", JsonValue.String(name)))));
        Assert.Equal(ControlResult.Refusal($"There’s no connector named “{shown}” in “Default”."),
            rig.Call("get_connector", Args(("name", JsonValue.String(name)))));
        Assert.Null(rig.State.AddEmptyCollection(name));
        Assert.Equal($"“{shown}” has no connectors.", rig.Call("list_connectors", Args(("collection", JsonValue.String(name))))?.Text);
        Assert.Equal(ControlResult.Refusal($"There’s no connector named “{shown}” in “{shown}”."),
            rig.Call("get_connector", Args(("name", JsonValue.String(name)), ("collection", JsonValue.String(name)))));
    }

    /// <summary>The heading of a connector names it and its collection as every other sentence does.</summary>
    [Fact]
    public void AConnectorsHeadingShowsItsNamesAsEscapes()
    {
        using var rig = new ControlRig();
        Assert.Null(rig.State.Upsert("n\u202Ex\\y", Github, null));
        var shown = rig.Call("get_connector", Args(("name", JsonValue.String("n\u202Ex\\y"))));
        Assert.NotNull(shown);
        Assert.Equal("""n\u{202E}x\\y · Default · on""", shown.Text.Split('\n')[0]);
    }

    /// <summary>The lists show a name one way only, as every sentence does: a backslash in it is doubled.</summary>
    [Fact]
    public void TheListsShowNamesOneWayOnly()
    {
        using var rig = new ControlRig();
        Assert.Null(rig.State.Upsert("""a\b""", Github, null));
        Assert.Null(rig.State.AddEmptyCollection("""c\d"""));
        Assert.Contains("""a\\b""", rig.Call("list_connectors")?.Text, StringComparison.Ordinal);
        Assert.Contains("""c\\d""", rig.Call("list_collections")?.Text, StringComparison.Ordinal);
    }

    /// <summary>A variable that names a program to run shows in full only in the confirmation dialog; what
    /// Claude reads of it is that it is set.</summary>
    [Fact]
    public void GetConnectorMasksAVariableThatRunsAProgram()
    {
        using var rig = new ControlRig();
        var entry = new McpEntry(JsonValue.Object(("command", JsonValue.String("git-mcp")),
            ("env", JsonValue.Object(("GIT_ASKPASS", JsonValue.String("/tmp/evil.sh"))))));
        Assert.Null(rig.State.Upsert("git", entry, null));
        var shown = rig.Call("get_connector", Args(("name", JsonValue.String("git"))));
        Assert.NotNull(shown);
        Assert.DoesNotContain("/tmp/evil.sh", shown.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", shown.Structured.CompactLine(), StringComparison.Ordinal);
        Assert.Equal(JsonValue.String("set"), At(shown.Structured, "config", "env", "GIT_ASKPASS"));
    }
}

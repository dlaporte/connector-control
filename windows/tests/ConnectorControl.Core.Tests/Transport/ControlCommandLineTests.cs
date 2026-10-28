using System.Text;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core.Tests.Transport;

/// <summary>Mirror: Tests/ConnectorControlStateTests/Transport/ControlCommandLineTests.swift</summary>
public class ControlCommandLineTests
{
    // Platform-forced differences: a working directory is a different kind of path on each platform, and
    // the expected paths are built with Path.GetFullPath against it, as the tool builds them; the missing
    // file throws a FileNotFoundException where the Mac mirror throws a CocoaError, each read through
    // AppState.Friendly.
    private static readonly string Cwd = OperatingSystem.IsWindows() ? @"C:\Users\me\work" : "/Users/me/work";
    private readonly Dictionary<string, byte[]> files = [];

    private ControlInvocation Parse(params string[] words) =>
        ControlCommandLine.Parse(words, Cwd, path => files.TryGetValue(path, out var data) ? data : throw new FileNotFoundException("missing.json"));

    private static ControlInvocation Call(string tool, params (string Key, JsonValue Value)[] arguments) =>
        new ControlInvocation.Call(tool, JsonValue.Object(arguments), false);

    private static ControlInvocation JsonCall(string tool, params (string Key, JsonValue Value)[] arguments) =>
        new ControlInvocation.Call(tool, JsonValue.Object(arguments), true);

    private static JsonValue S(string value) => JsonValue.String(value);

    private static string Full(string path) => Path.GetFullPath(path, Cwd);

    [Fact]
    public void EveryCommandRunsItsToolAndEveryToolHasACommand()
    {
        const string id = "claude_desktop_config.2026-10-28T12-00-00.000Z.json";
        (string[] Words, ControlInvocation Expected)[] table =
        [
            (["status"], Call("get_status")),
            (["doctor"], Call("check_tools")),
            (["connectors"], Call("list_connectors")),
            (["connectors", "--collection", "Team"], Call("list_connectors", ("collection", S("Team")))),
            (["show", "github"], Call("get_connector", ("name", S("github")))),
            (["show", "github", "--reveal", "--collection", "Team"],
                Call("get_connector", ("name", S("github")), ("reveal", JsonValue.Bool(true)), ("collection", S("Team")))),
            (["enable", "github"], Call("set_connector_enabled", ("name", S("github")), ("enabled", JsonValue.Bool(true)))),
            (["disable", "github"], Call("set_connector_enabled", ("name", S("github")), ("enabled", JsonValue.Bool(false)))),
            (["add", "gh", "--json", "{}"], Call("add_connector", ("name", S("gh")), ("config", JsonValue.Object()))),
            (["add", "gh", "--json", "{}", "--off"],
                Call("add_connector", ("name", S("gh")), ("config", JsonValue.Object()), ("enabled", JsonValue.Bool(false)))),
            (["edit", "gh", "--rename", "hub"], Call("update_connector", ("name", S("gh")), ("newName", S("hub")))),
            (["delete", "a", "b", "--collection", "Team"],
                Call("delete_connectors", ("names", JsonValue.Array([S("a"), S("b")])), ("collection", S("Team")))),
            (["collections"], Call("list_collections")),
            (["use", "Team"], Call("switch_collection", ("collection", S("Team")))),
            (["collection", "new", "Team"], Call("create_collection", ("name", S("Team")))),
            (["collection", "rename", "Team", "Crew"], Call("rename_collection", ("collection", S("Team")), ("newName", S("Crew")))),
            (["collection", "duplicate", "Team", "Copy"], Call("duplicate_collection", ("collection", S("Team")), ("newName", S("Copy")))),
            (["collection", "delete", "Team"], Call("delete_collection", ("collection", S("Team")))),
            (["import", "team.json", "--into", "Team", "--on-clash", "replace"],
                Call("import_document", ("path", S(Full("team.json"))), ("into", S("Team")), ("onClash", S("replace")))),
            (["import", "/x/team.json", "--new", "Crew"], Call("import_document", ("path", S(Full("/x/team.json"))), ("newCollection", S("Crew")))),
            (["subscribe", "../team.json", "--name", "Team"], Call("subscribe", ("path", S(Full("../team.json"))), ("name", S("Team")))),
            (["updates", "Team"], Call("get_pending_update", ("collection", S("Team")))),
            (["apply-update", "Team"], Call("apply_update", ("collection", S("Team")))),
            (["refresh", "Team"], Call("refresh_subscription", ("collection", S("Team")))),
            (["settings"], Call("get_settings")),
            (["settings", "set", "backups-to-keep", "30"], Call("set_setting", ("key", S("backups-to-keep")), ("value", S("30")))),
            (["backups"], Call("list_backups")),
            (["backups", "restore", id], Call("restore_backup", ("id", S(id)))),
            (["restart-claude"], Call("restart_claude")),
        ];
        var tools = new HashSet<string>();
        foreach (var (words, expected) in table)
        {
            var parsed = ControlCommandLine.Parse(words, Cwd, _ => []);
            Assert.True(expected == parsed, string.Join(" ", words));
            if (parsed is ControlInvocation.Call call)
            {
                tools.Add(call.Tool);
            }
        }
        Assert.Equal(ControlTools.Catalog.Select(tool => tool.Name).ToHashSet(), tools);
    }

    [Fact]
    public void JsonAnywhereAsksForTheStructure()
    {
        Assert.Equal(JsonCall("get_status"), Parse("status", "--json"));
        Assert.Equal(JsonCall("get_status"), Parse("--json", "status"));
        Assert.Equal(JsonCall("list_connectors", ("collection", S("Team"))), Parse("connectors", "--json", "--collection", "Team"));
    }

    [Fact]
    public void AddAndEditTakeTheirConfigAfterJsonOrFromAFile()
    {
        files[Full("github.json")] = Encoding.UTF8.GetBytes("""{"command":"npx","args":["-y","x"]}""");
        var config = JsonValue.Object(("command", S("npx")), ("args", JsonValue.Array([S("-y"), S("x")])));
        var usage = new ControlInvocation.Usage(ControlCommandLine.UsageError("add <name> --json <config> | --file <path> [--off]"));
        Assert.Equal(Call("add_connector", ("name", S("github")), ("config", config)),
            Parse("add", "github", "--json", """{"command":"npx","args":["-y","x"]}"""));
        Assert.Equal(JsonCall("add_connector", ("name", S("github")), ("config", config)),
            Parse("add", "github", "--json", """  {"command":"npx","args":["-y","x"]}""", "--json"));
        Assert.Equal(JsonCall("add_connector", ("name", S("github")), ("config", config)),
            Parse("add", "github", "--file", "github.json", "--json"));
        Assert.Equal(Call("update_connector", ("name", S("github")), ("config", config), ("newName", S("gh"))),
            Parse("edit", "github", "--file", "github.json", "--rename", "gh"));
        Assert.Equal(usage, Parse("add", "github"));
        Assert.Equal(usage, Parse("add", "github", "--json", "{}", "--file", "github.json"));
        Assert.Equal(new ControlInvocation.Usage(EditorModel.NotValidJson), Parse("add", "github", "--json", "{not json"));
        var missing = new FileNotFoundException("missing.json");
        Assert.Equal(new ControlInvocation.Usage(AppState.SourceUnreadableError("missing.json", AppState.Friendly(missing))),
            Parse("add", "github", "--file", "missing.json"));
    }

    [Fact]
    public void AReadErrorOfAnyKindNamesTheFileAndAFolderIsNamedWithoutItsSlash()
    {
        var odd = new NotSupportedException("odd");
        Assert.Equal(new ControlInvocation.Usage(AppState.SourceUnreadableError("odd.json", AppState.Friendly(odd))),
            ControlCommandLine.Parse(["add", "github", "--file", "odd.json"], Cwd, _ => throw odd));
        Assert.Equal(new ControlInvocation.Usage(AppState.SourceUnreadableError("sub", AppState.Friendly(new FileNotFoundException("missing.json")))),
            Parse("add", "github", "--file", "sub/"));
    }

    [Fact]
    public void HelpVersionAndTheRelay()
    {
        Assert.Equal(new ControlInvocation.Help(), Parse("help"));
        Assert.Equal(new ControlInvocation.Help(), Parse("--help"));
        Assert.Equal(new ControlInvocation.Help(), Parse("-h"));
        Assert.Equal(new ControlInvocation.Version(), Parse("--version"));
        Assert.Equal(new ControlInvocation.Version(), Parse("--json", "--version"));
        Assert.Equal(new ControlInvocation.Version(), Parse("--version", "--json"));
        Assert.Equal(new ControlInvocation.Relay(), Parse("mcp"));
        Assert.Equal(new ControlInvocation.Usage(ControlTools.HelpText()), ControlCommandLine.Parse([], Cwd, _ => []));
        Assert.Equal("connector-control 1.5.0", ControlCommandLine.VersionLine("1.5.0"));
    }

    [Fact]
    public void WhatTheToolCannotRunIsAUsageError()
    {
        Assert.Equal(new ControlInvocation.Usage(ControlCommandLine.UnknownCommandError("frobnicate")), Parse("frobnicate"));
        Assert.Equal(new ControlInvocation.Usage(ControlCommandLine.UnknownCommandError("collection frob")), Parse("collection", "frob"));
        Assert.Equal(new ControlInvocation.Usage(ControlCommandLine.UnknownCommandError("collection")), Parse("collection"));
        Assert.Equal(new ControlInvocation.Usage(ControlCommandLine.UsageError("show <name> [--reveal]")), Parse("show"));
        Assert.Equal(new ControlInvocation.Usage(ControlCommandLine.UsageError("status")), Parse("status", "extra"));
        Assert.Equal(new ControlInvocation.Usage(ControlCommandLine.UnknownOptionError("--reveal")), Parse("status", "--reveal"));
        Assert.Equal(new ControlInvocation.Usage(ControlCommandLine.MissingValueError("--collection")), Parse("connectors", "--collection"));
        // a combining mark after the dashes does not make the word a name
        Assert.Equal(new ControlInvocation.Usage(ControlCommandLine.UnknownOptionError("--\u0301x")), Parse("connectors", "--\u0301x"));
        Assert.Equal(new ControlInvocation.Usage(ControlCommandLine.UnknownOptionError("extra")), Parse("mcp", "extra"));
        Assert.Equal(new ControlInvocation.Usage(ControlCommandLine.UnknownOptionError("status")), Parse("help", "status"));
    }

    [Fact]
    public void AfterTwoDashesEveryWordIsAName()
    {
        Assert.Equal(Call("delete_connectors", ("names", JsonValue.Array([S("--odd"), S("plain")]))), Parse("delete", "--", "--odd", "plain"));
    }
}

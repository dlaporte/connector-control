using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// Mirror: Tests/ConnectorControlStateTests/CollectionsModelTests.swift.
/// The two panes of the Collections window: the collections as items, the selected one's connectors
/// as rows, the ⋯ menu's enablement, and the actions that go through the dialog seam.
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
        Assert.Null(state.CreateActiveCopy("Shared"));
        Assert.Null(state.CreateActiveCopy("Team"));
        state.SwitchCollection("Default");
        var file = File_(("Shared", Published("shared")), ("Team", Synced("team.json")));
        var cache = Cache([new("Team", Bound("/shared/team.json"))],
                          [new("Shared", new CollectionsLocalCache.PublishBinding("/tmp/share", null))]);
        h.Seed(state, file, cache);

        var model = h.CollectionsModel(state);
        Assert.Equal(["Default", "Shared", "Team"], model.Items.Select(i => i.Name));   // the chip menu's order
        Assert.Equal(["Default", "Shared", "Team"], model.Items.Select(i => i.Id));
        Assert.Equal([CollectionKind.Local, CollectionKind.Local, CollectionKind.Synced], model.Items.Select(i => i.Kind));
        Assert.Equal([true, false, false], model.Items.Select(i => i.IsActive));
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
        h.Seed(state, file, Cache(published: cache.Published));
        Assert.Equal([true, true, false], model.Items.Select(i => i.IsLocated));
        Assert.False(state.IsLocated("Team"));
        Assert.True(state.IsLocated("Default"));

        // Dispose cuts the republish: nothing repaints. What the kept list holds afterwards is not
        // asserted - nobody reads it once the window is gone, and pinning a stale value would make
        // a behaviour of it.
        model.Dispose();
        var before = repaints;
        state.PendingUpdates = new Dictionary<string, CollectionDiff>(StringComparer.Ordinal);
        Assert.Equal(before, repaints);
    }

    // MARK: rows

    /// <summary>
    /// Rows list in UTF-16 code-unit order, as they always have here: a character beyond U+FFFF
    /// comes before U+FF5E, where Swift's own <c>&lt;</c> puts it after.
    /// </summary>
    [Fact]
    public void RowsSortAsWindowsDoes()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        Assert.Null(state.Upsert("\uFF5E", AppStateHarness.LocalConnector("uvx"), null));
        Assert.Null(state.Upsert("\U0001F600", AppStateHarness.LocalConnector("uvx"), null));
        var model = h.CollectionsModel(state);
        Assert.Equal(["\U0001F600", "\uFF5E"], model.Rows.Select(r => r.Name));
        model.SetChecked("\uFF5E", true);
        model.SetChecked("\U0001F600", true);
        Assert.Null(state.CreateActiveCopy("Other"));   // a copy of the active one: both clash
        state.SwitchCollection("Default");
        Assert.Equal(["\U0001F600", "\uFF5E"], model.CheckedNamesClashing("Other"));
    }

    [Fact]
    public void RowsForASyncedCollectionAreLockedAndUncheckable()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        Assert.Null(state.CreateActiveCopy("Team"));
        Assert.Null(state.Upsert("github", new McpEntry(AppStateHarness.Remote("https://github.example/mcp")), null, "Team"));
        Assert.Null(state.Upsert("Ledger", AppStateHarness.LocalConnector("/usr/local/bin/node", "index.js"), null, "Team"));
        Assert.Null(state.Upsert("jira", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String("npx")),
            ("env", JsonValue.Object(("JIRA_TOKEN", JsonValue.String(Placeholder.Marker("JIRA_TOKEN"))))))), null, "Team"));
        Assert.Null(state.Upsert("notes", AppStateHarness.LocalConnector("uvx"), null, "Default"));
        h.MakeSynced(state, "Team", "/shared/team.json");

        var model = h.CollectionsModel(state, "Team");
        // Uppercase first: ordinal, the order the flyout lists the same connectors in.
        Assert.Equal(["Ledger", "github", "jira"], model.Rows.Select(r => r.Name));
        Assert.Equal(["Ledger", "github", "jira"], model.Rows.Select(r => r.Id));
        Assert.Equal(["node index.js", "github.example", "npx"], model.Rows.Select(r => r.Target));
        // Every row of a synced collection carries the lock.
        Assert.All(model.Rows, r => Assert.True(r.IsLocked));
        Assert.Equal([null, null, AppState.NeedsValueCaution("JIRA_TOKEN")], model.Rows.Select(r => r.Caution));

        // Nothing in a synced collection can be exported, so nothing in one can be ticked.
        model.SetChecked("github", true);
        Assert.All(model.Rows, r => Assert.False(r.Checked));
        Assert.Empty(model.CheckedNames);
        Assert.Empty(model.ExportIntentForChecked());

        // The same rows in a local collection do tick, and the ticks belong to that collection.
        model.Selected = "Default";
        Assert.Equal(["notes"], model.Rows.Select(r => r.Name));
        Assert.Equal(["uvx"], model.Rows.Select(r => r.Target));
        Assert.All(model.Rows, r => Assert.False(r.IsLocked));
        model.SetChecked("notes", true);
        Assert.Equal([true], model.Rows.Select(r => r.Checked));
        Assert.Equal(["notes"], model.CheckedNames);
        Assert.Equal(["notes"], model.ExportIntentForChecked());
        model.SetChecked("notes", false);
        Assert.Empty(model.ExportIntentForChecked());

        // Space flips a local row's tick both ways, and does nothing on a read-only one.
        model.ToggleChecked("notes");
        Assert.Equal(["notes"], model.CheckedNames);
        model.ToggleChecked("notes");
        Assert.Empty(model.CheckedNames);
        model.ToggleChecked("absent");
        Assert.Empty(model.CheckedNames);

        model.Selected = "Team";
        model.ToggleChecked("github");
        Assert.All(model.Rows, r => Assert.False(r.Checked));
        // Up and Down walk the rows in the order they are shown, and stop at either end.
        Assert.Equal("github", model.Neighbour("Ledger", 1));
        Assert.Equal("github", model.Neighbour("jira", -1));
        Assert.Null(model.Neighbour("Ledger", -1));
        Assert.Null(model.Neighbour("jira", 1));
        Assert.Null(model.Neighbour("absent", 1));

        // A click or Return opens the row in the collection the window is showing, not the active one.
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
        var model = h.CollectionsModel(state, "Default");
        var target = Assert.Single(model.Rows, r => r.Name == "leaky").Target;
        Assert.Equal("tool", target);
        foreach (var secret in secrets.Append("envsecret"))
        {
            Assert.DoesNotContain(secret, target);
        }
    }

    // MARK: the ⋯ menu's enablement

    [Fact]
    public void CollectionMenuEnablementFollowsTheSelection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateActiveCopy("Shared"));
        Assert.Null(state.CreateActiveCopy("Team"));
        state.SwitchCollection("Default");
        var file = File_(("Shared", Published("shared")), ("Team", Synced("team.json")));
        var located = Cache([new("Team", Bound("/shared/team.json"))],
                            [new("Shared", new CollectionsLocalCache.PublishBinding("/tmp/share", null))]);
        h.Seed(state, file, located);

        var model = h.CollectionsModel(state);
        Assert.Equal("Default", model.Selected);   // the selection starts on the active collection
        Assert.Empty(model.CheckedNames);          // nothing is ticked yet
        model.SetChecked("aws-mcp", true);
        Assert.Equal(["aws-mcp"], model.CheckedNames);
        Assert.False(model.CanRefresh);
        Assert.True(model.CanDelete);

        model.Selected = "Shared";
        Assert.Empty(model.CheckedNames);   // the ticks belonged to the collection that was showing
        Assert.False(model.CanRefresh);
        Assert.True(model.CanDelete);

        model.Selected = "Team";
        Assert.Empty(model.CheckedNames);
        Assert.True(model.CanRefresh);
        // A synced collection goes without taking the last local one with it.
        Assert.True(model.CanDelete);

        // Nothing to refresh until the file is found on this machine.
        h.Seed(state, file, Cache(published: located.Published));
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
        h.Seed(state, File_(("Default", Synced("default.json"))),
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
        Assert.Null(state.CreateActiveCopy("Team"));
        Assert.Null(state.Upsert("alpha", AppStateHarness.LocalConnector("/bin/alpha"), null, "Default"));
        var model = h.CollectionsModel(state, "Default");

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
        LeavesNoError("DeleteChecked with nothing ticked", model.DeleteChecked);

        model.SetChecked("alpha", true);
        LeavesNoError("CopyChecked into a collection it does not offer", () => model.CopyChecked("Default"));
        LeavesNoError("CopyCheckedIntoNewCollection, cancelled", () => model.CopyCheckedIntoNewCollection());
        Assert.Equal(["alpha"], model.CheckedNames);   // nothing was copied, so the ticks stay

        h.MakeSynced(state, "Team");
        model.Selected = "Team";
        LeavesNoError("MakeLocalCopy, cancelled", model.MakeLocalCopy);
        Assert.Equal(["Default", "Team"], state.CollectionNames);   // no prompt that was cancelled made anything
    }

    [Fact]
    public void NewCollectionStartsEmptyAndLeavesTheActiveCollectionAlone()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var model = h.CollectionsModel(state);
        Assert.True(state.Store.Collections["Default"].Mcps.ContainsKey("aws-mcp"));   // Default holds the seeded connectors
        var servers = h.ClaudeServers();
        var claudeBytes = File.ReadAllBytes(h.ClaudeConfigPath);

        // Cancelled: nothing is made and nothing changes.
        h.Dialogs.NextPromptAnswer = null;
        model.Create();
        Assert.Equal(["Default"], state.CollectionNames);
        Assert.Equal("Default", model.Selected);
        Assert.Null(model.LastError);

        h.Dialogs.NextPromptAnswer = "  Work  ";
        model.Create();
        Assert.Null(model.LastError);
        Assert.Equal(["Default", "Work"], state.CollectionNames);
        Assert.Empty(state.Store.Collections["Work"].Mcps);            // a new collection holds no connectors
        Assert.Equal(CollectionKind.Local, state.KindOf("Work"));
        Assert.Equal("Default", state.ActiveCollection);              // making a collection does not switch to it
        Assert.Equal("Work", model.Selected);                         // the window shows what it just made
        Assert.True(DictionaryEquality.Equal(servers, h.ClaudeServers()));   // Claude still runs what it ran
        Assert.Equal(claudeBytes, File.ReadAllBytes(h.ClaudeConfigPath));   // Claude's config is not rewritten

        // A name the store already holds is refused, and nothing else changes.
        h.Dialogs.NextPromptAnswer = "Work";
        model.Create();
        Assert.NotNull(model.LastError);
        Assert.Equal(["Default", "Work"], state.CollectionNames);
        Assert.Equal("Default", state.ActiveCollection);
        Assert.Equal(claudeBytes, File.ReadAllBytes(h.ClaudeConfigPath));
    }

    [Fact]
    public void CreateRenameDeleteGoThroughTheDialogs()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        Assert.Null(state.CreateActiveCopy("Work"));
        var model = h.CollectionsModel(state);
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
        Assert.Null(state.CreateActiveCopy("Team"));
        state.SwitchCollection("Default");
        h.MakeSynced(state, "Team", "/shared/team.json");
        Assert.True(state.IsSynced("Team"));
        var model = h.CollectionsModel(state, "Team");

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

    /// <summary>
    /// Stop Syncing, Refresh and Duplicate guard their own preconditions, as Make Local Copy and
    /// Stop Publishing do: out of turn, each does nothing, asks nothing, and clears the last error.
    /// </summary>
    [Fact]
    public void VerbsOutOfTurnDoNothingAndAskNothing()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateActiveCopy("Team"));
        state.SwitchCollection("Default");
        // Synced, but not located on this machine: nothing to refresh.
        h.MakeSynced(state, "Team");
        Assert.True(state.IsSynced("Team"));
        var model = h.CollectionsModel(state);

        model.Selected = "Default";
        PresetError(model, h);
        model.StopSyncing();
        Assert.Empty(h.Dialogs.Confirms);   // a local collection has nothing to stop syncing
        Assert.Null(model.LastError);

        model.Selected = "Team";
        Assert.False(model.CanRefresh);
        PresetError(model, h);
        model.Refresh();
        Assert.False(state.SourceErrors.ContainsKey("Team"));   // no read was attempted
        Assert.Null(model.LastError);

        PresetError(model, h);
        var prompted = h.Dialogs.Prompts.Count;
        Assert.False(model.Duplicate());   // a synced collection offers Make Local Copy instead
        Assert.Equal(prompted, h.Dialogs.Prompts.Count);   // no name is asked for
        Assert.Equal(["Default", "Team"], state.CollectionNames);
        Assert.Null(model.LastError);
    }

    [Fact]
    public void DeletingAPublishedCollectionAsksAboutTheFile()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        var folder = h.Dir.File("share");
        Directory.CreateDirectory(folder);
        Assert.Null(state.CreateActiveCopy("Shared"));
        Assert.Null(state.CreateActiveCopy("Consulting"));
        Assert.Null(state.StartPublishing("Shared", folder, PublishIntent.None));
        Assert.Null(state.StartPublishing("Consulting", folder, PublishIntent.None));
        var sharedFile = Path.Combine(folder, "shared.json");
        var consultingFile = Path.Combine(folder, "consulting.json");
        Assert.True(File.Exists(sharedFile));
        Assert.True(File.Exists(consultingFile));

        var model = h.CollectionsModel(state, "Shared");
        h.Dialogs.ConfirmAnswers.Enqueue(true);    // delete the collection
        h.Dialogs.ConfirmAnswers.Enqueue(false);   // keep the document
        model.Delete();
        Assert.Equal(
            [AppState.DeleteCollectionMessage("Shared"), CollectionsModel.DeletePublishedFileQuestion("shared.json")],
            h.Dialogs.Confirms.Select(c => c.Message));
        var fileQuestion = h.Dialogs.Confirms[^1];
        Assert.Equal(CollectionsModel.DeleteFileButton, fileQuestion.Primary);
        Assert.Equal(CollectionsModel.KeepFileButton, fileQuestion.Cancel);
        // Removing a file the team reads is never what Return does: Keep is the default, and
        // Remove, a click away, is marked as the destructive answer.
        Assert.True(fileQuestion.CancelIsDefault);
        Assert.True(fileQuestion.Destructive);
        Assert.DoesNotContain("Shared", state.CollectionNames);
        // Keep leaves the copy the team reads where it is.
        Assert.True(File.Exists(sharedFile));

        // The same question on its own, answered the other way.
        model.Selected = "Consulting";
        h.Dialogs.ConfirmAnswers.Enqueue(true);
        model.StopPublishing();
        Assert.Equal(CollectionsModel.DeletePublishedFileQuestion("consulting.json"), h.Dialogs.Confirms[^1].Message);
        Assert.True(h.Dialogs.Confirms[^1].CancelIsDefault);
        Assert.False(File.Exists(consultingFile));
        Assert.False(state.IsPublished("Consulting"));
        // Stop Publishing keeps the collection.
        Assert.Contains("Consulting", state.CollectionNames);
    }

    /// <summary>
    /// The Delete confirmation says what goes with the collection before the button is pressed:
    /// its connectors, which collection becomes active, that a synced source is left alone, and
    /// that a copy remains in Backups, for a collection that held anything to get back.
    /// </summary>
    [Fact]
    public void DeleteConfirmationSaysWhatGoesWithTheCollection()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        foreach (var name in new[] { "Empty", "One", "Shared", "Team", "Two" })
        {
            Assert.Null(state.CreateActiveCopy(name));
        }
        foreach (var (connector, collection) in new[] { ("alpha", "One"), ("alpha", "Team"), ("alpha", "Two"), ("beta", "Two") })
        {
            Assert.Null(state.Upsert(connector, AppStateHarness.LocalConnector("/bin/" + connector), null, collection));
        }
        h.MakeSynced(state, "Team", "/shared/team.json");
        h.Publish(state, "Shared");
        state.SwitchCollection("Two");
        var model = h.CollectionsModel(state);

        string? Informative(string collection)
        {
            model.Selected = collection;
            h.Dialogs.NextConfirm = false;
            model.Delete();
            return h.Dialogs.Confirms[^1].Informative;
        }
        // Nothing in it to get back from Backups.
        Assert.Equal("It has no connectors.", Informative("Empty"));
        Assert.Equal("Its 1 connector is deleted with it. Copies in other collections are not affected. "
                     + "A copy remains in Backups.", Informative("One"));
        // The first local collection with connectors, not the empty Default.
        Assert.Equal("Its 2 connectors are deleted with it. Copies in other collections are not affected. "
                     + "“One” becomes the active collection. A copy remains in Backups.", Informative("Two"));
        Assert.Equal("Its 1 connector is deleted with it. Copies in other collections are not affected. "
                     + "The source file is not changed. A copy remains in Backups.", Informative("Team"));
        // Publishing adds nothing here: the file has its own question.
        Assert.Equal("It has no connectors.", Informative("Shared"));
        Assert.Equal(["Default", "Empty", "One", "Shared", "Team", "Two"], state.CollectionNames);   // every one declined

        // Accepted: the collection the confirmation named is the one that becomes active.
        model.Selected = "Two";
        h.Dialogs.NextConfirm = true;
        model.Delete();
        Assert.Equal("One", state.ActiveCollection);
        Assert.True(state.Store.Collections["One"].Mcps.ContainsKey("alpha"));   // a copy in another collection stays
    }

    [Fact]
    public void AFailedPublishIsStoppedWithoutAskingAboutTheFile()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        var folder = h.Dir.File("share");
        Directory.CreateDirectory(folder);
        Assert.Null(state.CreateActiveCopy("Shared"));
        Assert.Null(state.StartPublishing("Shared", folder, PublishIntent.None));
        var file = Path.Combine(folder, "shared.json");
        Assert.True(File.Exists(file));

        var model = h.CollectionsModel(state, "Shared");
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
        Assert.Null(state.CreateActiveCopy("Consulting"));
        Assert.Null(state.StartPublishing("Consulting", folder, PublishIntent.None));
        model.Selected = "Consulting";
        state.PublishError = new CollectionPublishError("Shared", "the folder is read-only");
        h.Dialogs.ConfirmAnswers.Enqueue(false);
        model.StopPublishing();
        Assert.Equal([CollectionsModel.DeletePublishedFileQuestion("consulting.json")],
                     h.Dialogs.Confirms.Select(c => c.Message));
    }

    // MARK: connector count

    [Fact]
    public void ConnectorTallyIsSingularForOneConnector()
    {
        Assert.Equal("0 connectors", CollectionsModel.ConnectorTally(0));
        Assert.Equal("1 connector", CollectionsModel.ConnectorTally(1));
        Assert.Equal("14 connectors", CollectionsModel.ConnectorTally(14));
    }

    [Fact]
    public void ConnectorCountFollowsTheSelectedCollection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var model = h.CollectionsModel(state);
        Assert.Equal(CollectionsModel.ConnectorTally(3), model.ConnectorCount);

        var names = model.Rows.Select(r => r.Name).ToList();
        state.Delete(names.Skip(1).ToList());
        Assert.Equal(CollectionsModel.ConnectorTally(1), model.ConnectorCount);
        state.Delete([names[0]]);
        Assert.Equal(CollectionsModel.ConnectorTally(0), model.ConnectorCount);
    }

    [Fact]
    public void ConnectorCountSaysNothingElseExceptASourceThatCouldNotBeRead()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateActiveCopy("Shared"));
        Assert.Null(state.CreateActiveCopy("Team"));
        state.SwitchCollection("Default");
        var file = File_(("Shared", Published("shared")), ("Team", Synced("team.json")));
        var located = Cache([new("Team", Bound("/shared/team.json"))],
                            [new("Shared", new CollectionsLocalCache.PublishBinding("/Acme/mcp", null))]);
        h.Seed(state, file, located);

        // The active collection and a published one: the pills and the menu say the rest.
        var model = h.CollectionsModel(state);
        Assert.Equal(CollectionsModel.ConnectorTally(3), model.ConnectorCount);
        model.Selected = "Shared";
        Assert.Equal(CollectionsModel.ConnectorTally(3), model.ConnectorCount);

        // A synced one, up to date or with an update waiting: the banner speaks for the update.
        model.Selected = "Team";
        Assert.Equal(CollectionsModel.ConnectorTally(3), model.ConnectorCount);
        state.PendingUpdates = Pending("Team");
        Assert.Equal(CollectionsModel.ConnectorTally(3), model.ConnectorCount);

        // A source that could not be read has no banner, so the count carries the failure.
        state.SourceErrors = new Dictionary<string, string>(StringComparer.Ordinal) { ["Team"] = "team.json couldn’t be read" };
        Assert.Equal(CollectionsModel.ConnectorTally(3) + " · team.json couldn’t be read", model.ConnectorCount);

        // Not located: the Locate banner says so, and the count says nothing more.
        state.SourceErrors = new Dictionary<string, string>(StringComparer.Ordinal);
        h.Seed(state, file, Cache(published: located.Published));
        Assert.Equal(CollectionsModel.ConnectorTally(3), model.ConnectorCount);
        Assert.False(model.CanRefresh);

        // A synced entry that records no file name either - a hand-edited or foreign collections
        // file. Nothing asks to be located, but there is still no document to name or to read.
        h.Seed(state, File_(("Shared", Published("shared")), ("Team", new CollectionsFile.Entry(CollectionKind.Synced))),
            Cache(published: located.Published));
        Assert.True(state.IsLocated("Team"));   // nothing is waiting to be pointed at
        Assert.Equal(CollectionsModel.ConnectorTally(3), model.ConnectorCount);
        // Refresh would read a document nobody can point at.
        Assert.False(model.CanRefresh);
    }

    // MARK: selection

    [Fact]
    public void SelectionFallsBackToTheActiveCollectionWhenItsCollectionDisappears()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateActiveCopy("Work"));
        Assert.Null(state.CreateActiveCopy("Spare"));
        state.SwitchCollection("Default");
        var model = h.CollectionsModel(state);

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

    /// <summary>
    /// A double-click on the active collection, or Make Active on it: nothing to switch, so
    /// nothing is saved and nothing applied. The store file gone from disk is the witness, since
    /// any save would write it back. It succeeds all the same, so the last refusal goes.
    /// </summary>
    [Fact]
    public void SwitchingToTheActiveCollectionSavesNothing()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var model = h.CollectionsModel(state);
        PresetError(model, h);
        File.Delete(h.MasterStorePath);

        model.SwitchTo("Default");
        Assert.False(File.Exists(h.MasterStorePath));
        Assert.Equal("Default", state.ActiveCollection);
        Assert.Null(model.LastError);
    }

    // MARK: banner strip

    /// <summary>
    /// Default, active and published into a real folder; Team, synced with its file still to be
    /// found. The two banners the window can show therefore belong to different collections.
    /// </summary>
    private static void TwoBanners(AppStateHarness h, AppState state, string folder)
    {
        Assert.Null(state.CreateActiveCopy("Team"));
        state.SwitchCollection("Default");
        Directory.CreateDirectory(folder);
        h.Seed(state, File_(("Team", Synced("team.json")), ("Default", Published("default"))),
             Cache(published: [new("Default", new CollectionsLocalCache.PublishBinding(folder, null,
                 reviewedConnectors: state.Store.Collections["Default"].Mcps.Keys))]));
    }

    [Fact]
    public void TheBannerStripSpeaksOnlyForTheSelectedCollection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = h.Dir.File("pub");
        TwoBanners(h, state, folder);
        var model = h.CollectionsModel(state);

        // Team's file is missing, but the window is showing Default: the strip says nothing.
        Assert.Equal(new CollectionBanner.Locate("Team", "team.json"), state.CollectionBanner);
        Assert.Null(model.Banner);
        Assert.Null(model.BannerText);
        Assert.Null(model.BannerButton);
        Assert.False(model.HasBanner);   // C#-only: the XAML binds this bool; the Mac view switches on Banner itself
        Assert.False(model.BannerAction());

        model.Selected = "Team";
        // What the window's button switches on, so it never reads AppState's.
        Assert.Equal(new CollectionBanner.Locate("Team", "team.json"), model.Banner);
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
        var model = h.CollectionsModel(state);

        // The window is showing Default, which the locate banner is not about.
        var document = h.WriteDocument(CollectionDocumentSamples.DataTeam, "team.json");
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
        Assert.Equal("Edit “notion”", CollectionsModel.EditLabel("notion"));
        Assert.Equal("Make Active", CollectionsModel.MakeActiveAction);
        Assert.Equal("Read-only: synced from the collection’s author", CollectionsModel.LockedGlyphTooltip);
    }

    [Fact]
    public void TheSidebarChainNamesTheDocumentThisMachineReads()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateActiveCopy("Team"));
        state.SwitchCollection("Default");
        h.MakeSynced(state, "Team", "/Acme/mcp/team.json");
        var model = h.CollectionsModel(state);

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
        h.MakeSynced(state, "Team");
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
        Assert.Null(state.CreateActiveCopy("Work"));
        state.SwitchCollection("Default");
        var model = h.CollectionsModel(state, null, "aws-mcp");
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // The active collection is what is showing, so naming it again is the same selection -
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
        Assert.Null(state.CreateActiveCopy("Spare"));
        state.SwitchCollection("Default");
        var model = h.CollectionsModel(state, "Spare");

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
        Assert.Null(state.CreateActiveCopy("Spare"));
        state.SwitchCollection("Default");
        var model = h.CollectionsModel(state, "Spare");
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
        Assert.Null(state.CreateActiveCopy("Work"));
        Assert.Null(state.Upsert("extra", new McpEntry(AppStateHarness.Remote("https://extra.example/mcp")), null, "Work"));
        state.SwitchCollection("Default");
        var model = h.CollectionsModel(state);
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
        Assert.Contains(nameof(CollectionsModel.ConnectorCount), raised);

        // A store change the sidebar's items do not reflect leaves the list where it is.
        raised.Clear();
        state.SetEnabled("extra", false, "Work");
        Assert.DoesNotContain(nameof(CollectionsModel.Items), raised);
        Assert.Same(items, model.Items);

        // One that alters an item - which collection is active - replaces it.
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
        Assert.Null(state.CreateActiveCopy("Shared"));
        Assert.Null(state.StartPublishing("Shared", folder, PublishIntent.None));
        var model = h.CollectionsModel(state, "Shared");

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
    public void CopyDestinationsEnableNeitherTheSourceNorASyncedCollection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateActiveCopy("Other"));
        // Created first: the sidecar only annotates a collection already in the master list
        // (CollectionsFile.Reconciled), so seeding "Team" with nothing to annotate would leave it
        // dropped, and missing from CopyDestinations for not existing rather than disabled for
        // being synced.
        Assert.Null(state.CreateActiveCopy("Team"));
        h.MakeSynced(state, "Team");
        Assert.True(state.IsSynced("Team"));
        var model = h.CollectionsModel(state);

        model.Selected = "Default";
        // Not Default, which is the source, and not Team, which is synced.
        Assert.Equal(["Other"], model.CopyDestinations.Where(d => d.IsEnabled).Select(d => d.Name));
        model.Selected = "Other";
        Assert.Equal(["Default"], model.CopyDestinations.Where(d => d.IsEnabled).Select(d => d.Name));
    }

    /// <summary>
    /// The picker's menu: every collection but the source, a synced one listed but disabled so it
    /// can say why.
    /// </summary>
    [Fact]
    public void CopyDestinationsListEveryOtherCollectionAndDisableTheSyncedOnes()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateActiveCopy("Other"));
        Assert.Null(state.CreateActiveCopy("Team"));
        h.MakeSynced(state, "Team");
        Assert.True(state.IsSynced("Team"));
        var model = h.CollectionsModel(state);

        model.Selected = "Default";
        // Not Default, which is the source; Team is there, but cannot take copies.
        Assert.Equal(
            [new CollectionsModel.CopyDestination("Other", true), new CollectionsModel.CopyDestination("Team", false)],
            model.CopyDestinations);

        model.Selected = "Other";
        Assert.Equal(["Default", "Team"], model.CopyDestinations.Select(d => d.Name));   // the sidebar's order
        Assert.Equal([true, false], model.CopyDestinations.Select(d => d.IsEnabled));
    }

    /// <summary>Remove needs something ticked.</summary>
    [Fact]
    public void TheDeletePredicateFollowsTheTicks()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.Upsert("alpha", AppStateHarness.LocalConnector("/bin/alpha"), null, "Default"));
        var model = h.CollectionsModel(state);

        model.Selected = "Default";
        Assert.False(model.CanDeleteChecked);   // nothing ticked yet
        model.SetChecked("alpha", true);
        Assert.True(model.CanDeleteChecked);
    }

    /// <summary>
    /// The kind guard specifically, not just an empty tick set: Selected clears the ticks on
    /// every switch, so a leg that ticks a row and only then switches to the synced collection
    /// would find the predicate false regardless of the IsSynced term - the empty tick set
    /// alone would explain it. Reload does not clear ticks, so ticking first and letting the
    /// *same* collection turn synced underneath is the one path that isolates the guard.
    /// </summary>
    [Fact]
    public void TheDeletePredicateIsGatedBySyncSpecifically()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateActiveCopy("Team"));
        Assert.Null(state.Upsert("alpha", AppStateHarness.LocalConnector("/bin/alpha"), null, "Team"));
        var model = h.CollectionsModel(state, "Team", "alpha");
        Assert.True(model.CanDeleteChecked);   // still local, and something is ticked

        h.MakeSynced(state, "Team");
        Assert.True(state.IsSynced("Team"));
        Assert.Equal(["alpha"], model.CheckedNames);   // reload does not clear the ticks
        Assert.False(model.CanDeleteChecked);   // the guard, not an empty tick set, is what changed
    }

    /// <summary>
    /// Removing the ticked rows asks first, names the connector when there is one and the count
    /// when there are more, and always says a copy remains in Backups.
    /// </summary>
    [Fact]
    public void DeleteCheckedConfirmsAndCarriesTheBackupsSentence()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        foreach (var name in new[] { "alpha", "beta" })
        {
            Assert.Null(state.Upsert(name, AppStateHarness.LocalConnector("/bin/" + name), null, "Default"));
        }
        var model = h.CollectionsModel(state, "Default");

        // Declined: nothing goes, and the earlier error does not come back.
        PresetError(model, h);
        model.SetChecked("alpha", true);
        h.Dialogs.NextConfirm = false;
        model.DeleteChecked();
        Assert.Null(model.LastError);   // a declined confirmation clears the last error
        Assert.True(state.Store.Collections["Default"].Mcps.ContainsKey("alpha"));    // declined, so alpha stays
        Assert.True(state.Store.Collections["Default"].Mcps.ContainsKey("beta"));
        Assert.Equal(CollectionsModel.DeleteCheckedInformative, h.Dialogs.Confirms[^1].Informative);
        Assert.True(h.Dialogs.Confirms[^1].Destructive);
        Assert.Contains("alpha", h.Dialogs.Confirms[^1].Message);   // one connector is named

        // Accepted, two ticked: the count is stated and the ticks are dropped.
        h.Dialogs.NextConfirm = true;
        model.SetChecked("beta", true);
        model.DeleteChecked();
        Assert.False(state.Store.Collections["Default"].Mcps.ContainsKey("alpha"));   // both ticked rows went
        Assert.False(state.Store.Collections["Default"].Mcps.ContainsKey("beta"));
        Assert.Contains("2", h.Dialogs.Confirms[^1].Message);   // several are counted
        Assert.Empty(model.CheckedNames);   // and the ticks go with them
        Assert.Null(model.LastError);   // a removal that lands clears the stale error
    }

    /// <summary>
    /// Delete(names, collection) persists but never applies on its own; the caller applies only
    /// when the collection losing rows is the active one - the same rule AppState.SetEnabled follows.
    /// </summary>
    [Fact]
    public void DeleteCheckedAppliesOnlyWhenTheActiveCollectionLosesRows()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateActiveCopy("Work"));
        Assert.Null(state.Upsert("gamma", AppStateHarness.LocalConnector("/bin/gamma"), null, "Work"));
        state.SwitchCollection("Default");
        var model = h.CollectionsModel(state);
        h.Dialogs.NextConfirm = true;

        // An enabled connector in the active collection that has not been applied yet: an apply
        // from the inactive leg below would write it, so that leg can catch an unconditional one.
        Assert.Null(state.Upsert("delta", AppStateHarness.LocalConnector("/bin/delta"), null, "Default"));
        Assert.True(state.Store.Collections["Default"].Mcps["delta"].Enabled);
        Assert.False(h.ClaudeServers().ContainsKey("delta"));   // upserted, not applied

        // Inactive collection: the write lands, but Claude's config is untouched.
        var before = h.ClaudeServers();
        model.Selected = "Work";
        model.SetChecked("gamma", true);
        model.DeleteChecked();
        Assert.False(state.Store.Collections["Work"].Mcps.ContainsKey("gamma"));   // removed from the store
        // Work was never active, so nothing Claude runs has changed.
        Assert.True(DictionaryEquality.Equal(before, h.ClaudeServers()));
        Assert.False(h.ClaudeServers().ContainsKey("delta"));   // no apply ran, so the pending connector is still unwritten

        // The active collection: the same call does apply.
        Assert.True(before.ContainsKey("aws-mcp"));   // there before, so its absence below is the apply's doing
        model.Selected = "Default";
        model.SetChecked("aws-mcp", true);
        model.DeleteChecked();
        Assert.False(state.Store.Collections["Default"].Mcps.ContainsKey("aws-mcp"));
        Assert.False(h.ClaudeServers().ContainsKey("aws-mcp"));   // the active collection changed, so Claude's config follows
    }

    /// <summary>
    /// The copy lands in the target, disabled, and the ticks go with it - the same clearing
    /// DeleteChecked does on success. Every copy arrives disabled, so this never applies.
    /// </summary>
    [Fact]
    public void CopyCheckedCopiesIntoTheTargetClearsTicksAndDoesNotApply()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        // Created first: CreateActiveCopy starts a collection as a copy of whichever one is
        // active when it is made, so making Spare before alpha exists keeps alpha out of that
        // starting snapshot - otherwise the copy below would collide with it and land as
        // "alpha 2" instead, leaving the original untouched and this test green for the wrong
        // reason.
        Assert.Null(state.CreateActiveCopy("Spare"));
        Assert.Null(state.Upsert("alpha", AppStateHarness.LocalConnector("/bin/alpha"), null, "Default"));
        var model = h.CollectionsModel(state, "Default", "alpha");

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
    /// The two early-outs CopyChecked documents: a target CopyDestinations does not enable -
    /// the source itself, or a synced collection - and an empty tick set. Both return false without
    /// reaching AppState.MakeLocalCopy, so nothing lands anywhere and the ticks are left standing.
    /// The last error is cleared, so the window does not show it again as if this copy had failed.
    /// </summary>
    [Fact]
    public void CopyCheckedRefusesATargetCopyDestinationsDoesNotEnable()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        // Created first, for the same reason as the success test above, and so seeding the
        // sidecar afterwards has something in the master list to annotate (CollectionsFile.Reconciled).
        Assert.Null(state.CreateActiveCopy("Team"));
        Assert.Null(state.Upsert("alpha", AppStateHarness.LocalConnector("/bin/alpha"), null, "Default"));
        h.MakeSynced(state, "Team");
        var model = h.CollectionsModel(state, "Default", "alpha");
        PresetError(model, h);

        Assert.False(model.CopyChecked("Default"));   // the source is not a target
        Assert.False(model.CopyChecked("Team"));      // a synced collection is not a target either
        Assert.Equal(["alpha"], model.CheckedNames);   // both are unreachable early-outs, so the ticks stand
        Assert.Null(model.LastError);   // the earlier error is not shown again
        Assert.False(state.Store.Collections["Team"].Mcps.ContainsKey("alpha"));   // nothing landed in Team

        // The empty tick set, into a target that is enabled.
        Assert.Null(state.AddEmptyCollection("Other"));
        var idle = h.CollectionsModel(state, "Default");
        Assert.Contains(new CollectionsModel.CopyDestination("Other", true), idle.CopyDestinations);   // the target itself is open
        PresetError(idle, h);
        Assert.False(idle.CopyChecked("Other"));   // nothing ticked, so nothing to copy
        Assert.Null(idle.LastError);   // the earlier error is not shown again here either
        Assert.Empty(state.Store.Collections["Other"].Mcps);   // nothing landed in Other
    }

    [Fact]
    public void CopyCheckedRefusesWhenNothingIsTicked()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateActiveCopy("Spare"));
        var before = state.Store.Collections["Spare"];
        var model = h.CollectionsModel(state, "Default");

        Assert.False(model.CopyChecked("Spare"));   // nothing ticked, so there is nothing to copy
        Assert.Equal(before, state.Store.Collections["Spare"]);   // and nothing about Spare changed
    }

    /// <summary>
    /// A copy that lands says so on the count line, naming how many landed and where, since the
    /// window stays on the source and the ticks clear. The note lasts until the next tick, the next
    /// action or another collection; a copy that does not land, or one whose clashes were all
    /// skipped, says nothing. The count line repaints each time.
    /// </summary>
    [Fact]
    public void ACopyConfirmsOnTheCountLineUntilTheNextTickActionOrSelection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateActiveCopy("Spare"));
        Assert.Null(state.Upsert("alpha", AppStateHarness.LocalConnector("/bin/alpha"), null, "Default"));
        Assert.Null(state.Upsert("beta", AppStateHarness.LocalConnector("/bin/beta"), null, "Default"));
        var model = h.CollectionsModel(state, "Default", "alpha");
        var countRaises = 0;
        model.PropertyChanged += (_, e) => countRaises += e.PropertyName == nameof(CollectionsModel.ConnectorCount) ? 1 : 0;
        var tally = CollectionsModel.ConnectorTally(state.Store.Collections["Default"].Mcps.Count);
        Assert.Null(model.CopyConfirmation);
        Assert.Equal(tally, model.ConnectorCount);

        Assert.True(model.CopyChecked("Spare"));
        Assert.Equal("Copied 1 connector to “Spare”", model.CopyConfirmation);
        Assert.Equal(tally + " · Copied 1 connector to “Spare”", model.ConnectorCount);
        Assert.True(countRaises > 0);   // the count line repaints

        countRaises = 0;
        model.SetChecked("alpha", true);
        Assert.Null(model.CopyConfirmation);   // the next tick clears it
        Assert.Equal(tally, model.ConnectorCount);
        Assert.True(countRaises > 0);

        model.SetChecked("beta", true);
        h.Dialogs.NextPromptAnswer = "Fresh";
        Assert.True(model.CopyCheckedIntoNewCollection());
        Assert.Equal("Copied 2 connectors to “Fresh”", model.CopyConfirmation);   // Copy to ▸ New Collection confirms too
        model.Selected = "Spare";
        Assert.Null(model.CopyConfirmation);   // another collection clears it

        model.Selected = "Default";
        model.SetChecked("alpha", true);
        Assert.True(model.CopyChecked("Spare"));
        Assert.NotNull(model.CopyConfirmation);
        h.Dialogs.NextPromptAnswer = null;
        model.Rename();
        Assert.Null(model.CopyConfirmation);   // the next action clears it, even one cancelled

        // Spare now holds alpha: skipping the clash copies nothing, so there is nothing to confirm.
        model.SetChecked("alpha", true);
        Assert.True(model.CopyChecked("Spare", new Dictionary<string, ImportChoice> { ["alpha"] = ImportChoice.Skip }));
        Assert.Null(model.CopyConfirmation);   // a skipped clash is not counted

        // Some clashes answered Skip: only what landed is counted. beta is new to Spare, alpha clashes.
        model.SetChecked("alpha", true);
        model.SetChecked("beta", true);
        Assert.True(model.CopyChecked("Spare", new Dictionary<string, ImportChoice> { ["alpha"] = ImportChoice.Skip }));
        Assert.Equal("Copied 1 connector to “Spare”", model.CopyConfirmation);   // the skipped clash is not counted
        // Spare now holds both: replace one clash, skip the other, and the replaced one is counted.
        model.SetChecked("alpha", true);
        model.SetChecked("beta", true);
        Assert.True(model.CopyChecked("Spare", new Dictionary<string, ImportChoice> { ["alpha"] = ImportChoice.Replace, ["beta"] = ImportChoice.Skip }));
        Assert.Equal("Copied 1 connector to “Spare”", model.CopyConfirmation);   // a Replace lands, a Skip does not

        // A copy that does not land confirms nothing.
        model.SetChecked("alpha", true);
        h.Dialogs.NextPromptAnswer = "Spare";
        Assert.False(model.CopyCheckedIntoNewCollection());
        Assert.NotNull(model.LastError);
        Assert.Null(model.CopyConfirmation);
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
        Assert.Null(state.Upsert("alpha", AppStateHarness.LocalConnector("/bin/alpha"), null, "Default"));
        Assert.Null(state.Upsert("beta", AppStateHarness.LocalConnector("/bin/beta"), null, "Default"));
        Assert.True(state.Store.Collections["Default"].Mcps.ContainsKey("aws-mcp"));
        var model = h.CollectionsModel(state, "Default", "alpha", "beta");
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
        Assert.Null(state.Upsert("alpha", AppStateHarness.LocalConnector("/bin/alpha"), null, "Default"));
        var model = h.CollectionsModel(state, "Default", "alpha");

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
        Assert.Null(state.CreateActiveCopy("Work"));
        Assert.Null(state.Upsert("alpha", AppStateHarness.LocalConnector("/bin/alpha"), null, "Default"));
        state.SwitchCollection("Default");
        var model = h.CollectionsModel(state, "Default", "alpha");

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
        Assert.Null(state.CreateActiveCopy("Spare"));
        state.SwitchCollection("Default");
        foreach (var name in new[] { "zeta", "alpha", "beta" })
        {
            Assert.Null(state.Upsert(name, AppStateHarness.LocalConnector("/bin/" + name), null, "Default"));
        }
        foreach (var name in new[] { "zeta", "alpha" })
        {
            Assert.Null(state.Upsert(name, AppStateHarness.LocalConnector("/bin/other"), null, "Spare"));
        }
        var model = h.CollectionsModel(state, "Default", "zeta", "beta", "alpha");

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
        Assert.Null(state.CreateActiveCopy("Shared"));
        Assert.Null(state.CreateActiveCopy("Team"));
        Assert.Null(state.CreateActiveCopy("Plain"));
        state.SwitchCollection("Default");
        var file = File_(("Shared", Published("shared")), ("Team", Synced("team.json")));
        var cache = Cache([new("Team", Bound("/shared/team.json"))],
                          [new("Shared", new CollectionsLocalCache.PublishBinding("/tmp/share", null))]);
        h.Seed(state, file, cache);

        var model = h.CollectionsModel(state, "Default");
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

    /// <summary>The two entries the model dims say so themselves; every other entry it lists applies.</summary>
    [Fact]
    public void OnlyExportAllAndDeleteCanArriveDimmed()
    {
        Assert.False(CollectionsModel.IsEnabled(new CollectionsModel.MenuEntry.ExportAll(false)));
        Assert.True(CollectionsModel.IsEnabled(new CollectionsModel.MenuEntry.ExportAll(true)));
        Assert.False(CollectionsModel.IsEnabled(new CollectionsModel.MenuEntry.Delete(false)));
        Assert.True(CollectionsModel.IsEnabled(new CollectionsModel.MenuEntry.Delete(true)));
        CollectionsModel.MenuEntry[] rest =
        [
            new CollectionsModel.MenuEntry.MakeActive(), new CollectionsModel.MenuEntry.Rename(),
            new CollectionsModel.MenuEntry.Duplicate(), new CollectionsModel.MenuEntry.StartPublishing(),
            new CollectionsModel.MenuEntry.PublishingSettings(), new CollectionsModel.MenuEntry.StopPublishing(),
            new CollectionsModel.MenuEntry.ShowPublishedFile(), new CollectionsModel.MenuEntry.MakeLocalCopy(),
            new CollectionsModel.MenuEntry.Refresh(), new CollectionsModel.MenuEntry.ShowSourceFile(),
            new CollectionsModel.MenuEntry.StopSyncing(),
        ];
        Assert.All(rest, entry => Assert.True(CollectionsModel.IsEnabled(entry)));
    }

    [Fact]
    public void CollectionMenuForLocalActiveUnpublished()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        // A second local collection, so the common case is not also the last-collection case.
        Assert.Null(state.CreateActiveCopy("Work"));
        state.SwitchCollection("Default");
        var model = h.CollectionsModel(state, "Default");
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
        Assert.Null(state.CreateActiveCopy("Shared"));
        Assert.Null(state.StartPublishing("Shared", folder, PublishIntent.None));
        state.SwitchCollection("Default");   // Shared stays, now not the active collection

        var model = h.CollectionsModel(state, "Shared");
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
        Assert.Null(state.CreateActiveCopy("Team"));
        state.SwitchCollection("Default");
        h.MakeSynced(state, "Team");
        var model = h.CollectionsModel(state, "Team");

        // Located via a real file, the same flow the banner strip's Locate button drives.
        var document = h.WriteDocument(CollectionDocumentSamples.DataTeam, "team.json");
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
        Assert.Null(state.CreateActiveCopy("Team"));
        state.SwitchCollection("Default");
        h.MakeSynced(state, "Team");
        var model = h.CollectionsModel(state, "Team");

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
        var model = h.CollectionsModel(state);
        // Only "Default" exists.
        Assert.False(model.CanDelete);
        Assert.Contains(model.CollectionMenu, e => e is CollectionsModel.MenuEntry.Delete { Enabled: false });
    }

    [Fact]
    public void DuplicateKeepsEachConnectorAsItIsWithoutActivatingAndReportsAClash()
    {
        // Not seeded: CreateActiveCopy copies whichever collection is active when it runs, and
        // Default is still active at that point.
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        Assert.Null(state.CreateActiveCopy("Team"));
        Assert.Null(state.Upsert("alpha", AppStateHarness.LocalConnector("/bin/alpha"), null, "Team"));
        Assert.Null(state.Upsert("beta", AppStateHarness.LocalConnector("/bin/beta"), null, "Team"));
        state.SetEnabled("beta", false, "Team");
        // A connector Team itself took in as a copy, so it has provenance of its own to keep.
        Assert.Null(state.Upsert("gamma", AppStateHarness.LocalConnector("/bin/gamma"), null, "Default"));
        Assert.Null(state.MakeLocalCopy(["gamma"], "Default", "Team"));
        var gammaProvenance = state.CollectionsFile.Collections["Team"].Provenance["gamma"];
        state.SwitchCollection("Default");
        var model = h.CollectionsModel(state, "Team");

        // Cancelled: nothing changes.
        h.Dialogs.NextPromptAnswer = null;
        Assert.False(model.Duplicate());
        Assert.Equal(["Default", "Team"], state.CollectionNames);

        var before = h.ClaudeServers();
        h.Dialogs.NextPromptAnswer = "  Team Copy  ";
        Assert.True(model.Duplicate());
        Assert.Equal(new FakeDialogs.PromptCall(AppState.NewCollectionTitle, ""), h.Dialogs.Prompts[^1]);
        var copy = state.Store.Collections["Team Copy"];   // the store trimmed the name
        Assert.Equal(state.Store.Collections["Team"], copy);   // every connector as it stands, its switch included
        Assert.True(copy.Mcps["alpha"].Enabled);
        Assert.False(copy.Mcps["beta"].Enabled);
        // It is the user's own copy: nothing says it was imported, and what Team says is kept.
        var provenance = Assert.Single(state.CollectionsFile.Collections["Team Copy"].Provenance);
        Assert.Equal("gamma", provenance.Key);
        Assert.Equal(gammaProvenance, provenance.Value);
        Assert.Equal(CollectionKind.Local, state.KindOf("Team Copy"));
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
        Assert.Null(state.CreateActiveCopy("Shared"));
        Assert.Null(state.CreateActiveCopy("Team"));
        state.SwitchCollection("Default");
        h.MakeSynced(state, "Team");

        var folder = h.Dir.File("share");
        Directory.CreateDirectory(folder);
        Assert.Null(state.StartPublishing("Shared", folder, PublishIntent.None));

        var model = h.CollectionsModel(state);

        model.Selected = "Shared";
        var published = model.PublishedFilePath;
        Assert.NotNull(published);
        Assert.EndsWith("shared." + CollectionDocument.FileExtension, published);

        model.Selected = "Default";
        Assert.Null(model.PublishedFilePath);   // not published from here

        model.Selected = "Team";
        Assert.Null(model.SourceFilePath);   // not located yet
        var document = h.WriteDocument(CollectionDocumentSamples.DataTeam, "team.json");
        Assert.Null(model.LocateSource(document));
        Assert.Equal(state.SourceLocation("Team"), model.SourceFilePath);
        Assert.Equal(document, model.SourceFilePath);
    }

    // MARK: sidebar and the header's +

    [Fact]
    public void AddConnectorAffordanceFollowsSyncAndTargetsTheSelectedCollection()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        Assert.Null(state.CreateActiveCopy("Team"));
        state.SwitchCollection("Default");
        h.MakeSynced(state, "Team");
        var model = h.CollectionsModel(state);

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

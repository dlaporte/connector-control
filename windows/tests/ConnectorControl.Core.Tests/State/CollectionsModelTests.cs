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

    private static CollectionsLocalCache.SyncedBinding Bound(string? path) => new(path, null);

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
        Assert.Equal(["local · node", "remote", "local · npx"], model.Rows.Select(r => r.TypeText));
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
        Assert.Equal(["local · uvx"], model.Rows.Select(r => r.TypeText));
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
        Assert.False(model.CanMakeLocalCopy);
        Assert.False(model.CanStopSyncing);
        Assert.False(model.CanStopPublishing);
        Assert.True(model.CanDelete);

        model.Selected = "Shared";
        Assert.False(model.CanExport);    // the ticks belonged to the collection that was showing
        // Published from here, and still offered: reopening the dialog shows the record and
        // pressing Publish again updates what is shared, so both links stand side by side.
        Assert.True(model.CanPublish);
        Assert.True(model.CanStopPublishing);
        Assert.False(model.CanRefresh);
        Assert.True(model.CanDelete);

        model.Selected = "Team";
        Assert.False(model.CanExport);
        Assert.False(model.CanPublish);   // a synced collection has an author elsewhere
        Assert.False(model.CanStopPublishing);
        Assert.True(model.CanRefresh);
        Assert.True(model.CanMakeLocalCopy);
        Assert.True(model.CanStopSyncing);
        // A synced collection goes without taking the last local one with it.
        Assert.True(model.CanDelete);

        // Nothing to refresh until the file is found on this machine.
        Seed(h, state, file, Cache(published: located.Published));
        Assert.False(model.CanRefresh);
        Assert.True(model.CanMakeLocalCopy);

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

        // Declined: the collection stays.
        h.Dialogs.NextConfirm = false;
        model.Delete();
        var asked = h.Dialogs.Confirms[^1];
        Assert.Equal(AppState.DeleteCollectionMessage("Team B"), asked.Message);
        Assert.Equal(AppState.DeleteButton, asked.Primary);
        Assert.True(asked.Destructive);
        Assert.Equal(["Default", "Team B", "Work"], state.CollectionNames);

        h.Dialogs.NextConfirm = true;
        model.Delete();
        Assert.Equal(["Default", "Work"], state.CollectionNames);
        Assert.Equal(2, h.Dialogs.Confirms.Count);   // an unpublished collection is asked about once
        Assert.Equal(state.ActiveCollection, model.Selected);
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
        Assert.False(model.CanStopPublishing);
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
}

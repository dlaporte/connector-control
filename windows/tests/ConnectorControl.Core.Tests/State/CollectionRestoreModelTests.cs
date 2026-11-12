using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// The Restore dialog's Collections tab.
///
/// Mirror: Tests/ConnectorControlStateTests/CollectionRestoreModelTests.swift. The Mac's listing-failure test is
/// its alone, as RestoreModelTests' is: it takes the backups folder's permissions away.
/// </summary>
public class CollectionRestoreModelTests
{
    /// <summary><paramref name="store"/> saved where the backups are, as a master-list backup holds it. Named older
    /// than any backup a save takes.</summary>
    private static string Backup(AppStateHarness h, MasterStore store, string name = "mcps.2026-01-01T00-00-00-000Z.json")
    {
        Directory.CreateDirectory(h.BackupsDir);
        var path = Path.Combine(h.BackupsDir, name);
        MasterStoreIO.Save(store, path);
        return path;
    }

    /// <summary>Chooses the backup at <paramref name="path"/> as the dialog's list does: by the path the listing
    /// gave.</summary>
    private static void Choose(string path, CollectionRestoreModel model)
    {
        model.Selection = model.Backups.FirstOrDefault(b => Path.GetFileName(b.Path) == Path.GetFileName(path))?.Path;
        Assert.NotNull(model.Selection);
    }

    private static McpEntry Entry(string command, bool on = true) => new(on, JsonValue.Object(("command", JsonValue.String(command))));

    /// <summary>A backup taken with Default as the harness seeds it, Gone (one connector, since deleted), Personal
    /// (one connector, one added since) and Data team (subscribed then and now).</summary>
    private static string StagedBackup(AppStateHarness h, AppState state)
    {
        Assert.Null(state.AddEmptyCollection("Gone"));
        Assert.Null(state.Upsert("g", Entry("g"), null, "Gone"));
        Assert.Null(state.AddEmptyCollection("Personal"));
        Assert.Null(state.Upsert("notes", Entry("notes", on: false), null, "Personal"));
        h.Subscribe(state, CollectionDocumentSamples.DataTeam);
        var path = Backup(h, state.Store);
        Assert.Null(state.DeleteCollection("Gone"));
        Assert.Null(state.Upsert("added", Entry("added"), null, "Personal"));
        return path;
    }

    [Fact]
    public void ListsBackupsNewestFirstWithTheirLabels()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var older = Backup(h, state.Store);
        var newer = Backup(h, state.Store, "mcps.2026-01-02T00-00-00-000Z.json");
        var model = new CollectionRestoreModel(state, h.Dialogs);
        model.Load();
        Assert.Equal([Path.GetFileName(newer), Path.GetFileName(older)], model.Backups.Select(b => Path.GetFileName(b.Path)));
        var labels = BackupManager.Labels([newer, older]);
        Assert.Equal([labels[Path.GetFileName(newer)], labels[Path.GetFileName(older)]], model.BackupLabels);
        Assert.Null(model.Selection);   // no backup is chosen when the tab opens
        Assert.Empty(model.Rows);
        Assert.False(model.CanRestore);
        Assert.Null(model.RestoreError);
    }

    /// <summary>Each collection the chosen backup holds, by name, with its connector count and how it stands now;
    /// only one deleted since or differing from now can be restored. Choosing another backup clears the chosen
    /// collection.</summary>
    [Fact]
    public void EachCollectionSaysHowItStandsNow()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = StagedBackup(h, state);
        var held = MasterStoreIO.Read(path)!;
        string Count(string name)
        {
            var mcps = held.Collections[name].Mcps;
            return ControlTools.ConnectorCountText(mcps.Count, mcps.Values.Count(e => e.Enabled));
        }
        var other = Backup(h, state.Store, "mcps.2026-01-02T00-00-00-000Z.json");
        var model = new CollectionRestoreModel(state, h.Dialogs);
        model.Load();
        Choose(path, model);

        Assert.Equal(["Data team", "Default", "Gone", "Personal"], model.Rows.Select(r => r.Name));
        Assert.Equal([CollectionRestoreModel.RowState.Subscribed, CollectionRestoreModel.RowState.Same,
                      CollectionRestoreModel.RowState.Gone, CollectionRestoreModel.RowState.Differs], model.Rows.Select(r => r.State));
        Assert.Equal([
            Count("Data team") + " · subscribed now",
            Count("Default") + " · same as now",
            "1 connector, 1 on · deleted since",
            "1 connector, 0 on · differs from now",
        ], model.Rows.Select(r => r.Detail));
        Assert.Equal("Gone · 1 connector, 1 on · deleted since", model.Rows[2].SpokenName);
        Assert.Null(model.RestoreError);

        Assert.False(model.CanRestore);   // no collection is chosen yet
        foreach (var (name, restorable) in new[] { ("Data team", false), ("Default", false), ("Gone", true), ("Personal", true) })
        {
            model.Collection = name;
            Assert.Equal(restorable, model.CanRestore);
        }

        Choose(other, model);
        Assert.Null(model.Collection);   // another backup clears the chosen collection
        Assert.False(model.CanRestore);
        Assert.Equal(["Data team", "Default", "Personal"], model.Rows.Select(r => r.Name));
    }

    /// <summary>A row read when the backup was chosen is read again when its collection is chosen, so one that changed
    /// in between shows how it stands now, and the choice stays.</summary>
    [Fact]
    public void ChoosingACollectionRereadsItsRowsAgainstTheStoreNow()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = StagedBackup(h, state);
        var model = new CollectionRestoreModel(state, h.Dialogs);
        model.Load();
        Choose(path, model);
        Assert.Equal(CollectionRestoreModel.RowState.Gone, model.Rows.Single(r => r.Name == "Gone").State);

        Assert.Null(state.AddEmptyCollection("Gone"));
        Assert.Null(state.Upsert("g", Entry("g"), null, "Gone"));
        Assert.Equal(CollectionRestoreModel.RowState.Gone, model.Rows.Single(r => r.Name == "Gone").State);   // not read again until chosen

        model.Collection = "Gone";
        Assert.Equal(CollectionRestoreModel.RowState.Same, model.Rows.Single(r => r.Name == "Gone").State);
        Assert.Equal("Gone", model.Collection);
        Assert.False(model.CanRestore);
    }

    /// <summary>The confirmation names the collection and the backup's time, and lists every line; then, after a
    /// blank line, that a gone collection is created again, or that Claude's config is updated too for the active
    /// one, or nothing more. A gone collection that held no connectors says only that.</summary>
    [Fact]
    public void TheConfirmationListsEachLineAndWhatElseHappens()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.AddEmptyCollection("Empty"));
        var path = StagedBackup(h, state);
        Assert.Null(state.DeleteCollection("Empty"));
        state.SetEnabled(state.Store.Collections["Default"].Mcps.Keys.Order(StringComparer.Ordinal).First(), false);
        var model = new CollectionRestoreModel(state, h.Dialogs);
        model.Load();
        Choose(path, model);
        var label = model.Backups.First(b => Path.GetFileName(b.Path) == Path.GetFileName(path)).Label;
        var defaultLines = state.CollectionRestorePlan("Default", Path.GetFileName(path), File.ReadAllBytes(path)).Lines();
        Assert.StartsWith("~ ", defaultLines[0], StringComparison.Ordinal);

        h.Dialogs.NextConfirm = false;
        var closed = 0;
        model.CloseRequested += () => closed++;
        foreach (var (collection, informative) in new[]
        {
            ("Gone", "+ g (on)\n\n“Gone” no longer exists, so it is created again."),
            ("Default", string.Join("\n", defaultLines) + "\n\nClaude is running this collection, so Claude’s config is updated too."),
            ("Personal", "− added\n= 1 unchanged"),
            ("Empty", "“Empty” no longer exists, so it is created again."),
        })
        {
            model.Collection = collection;
            Assert.True(model.CanRestore, collection);
            Assert.False(model.Restore());
            Assert.Equal(new FakeDialogs.ConfirmCall($"Restore “{collection}” as it was at {label}?", informative, "Restore", "Cancel", true),
                h.Dialogs.Confirms[^1]);
        }
        Assert.Equal(4, h.Dialogs.Confirms.Count);
        Assert.Equal(0, closed);
        Assert.False(state.Store.Collections.ContainsKey("Gone"));   // cancelled: nothing restored
        Assert.True(state.Store.Collections["Personal"].Mcps.ContainsKey("added"));
    }

    /// <summary>What is restored is the backup as the tab read it when it loaded, not the file as it is by the time
    /// the user confirms.</summary>
    [Fact]
    public void ARestoreLandsTheBackupAsTheTabReadIt()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.AddEmptyCollection("Personal"));
        Assert.Null(state.Upsert("notes", Entry("notes"), null, "Personal"));
        var personal = state.Store.Collections["Personal"].Clone();
        var path = Backup(h, state.Store);
        Assert.Null(state.DeleteCollection("Personal"));
        var model = new CollectionRestoreModel(state, h.Dialogs);
        model.Load();
        var closed = 0;
        model.CloseRequested += () => closed++;

        var rewritten = MasterStoreIO.Read(path)!;
        rewritten.Collections["Personal"].Mcps["swapped"] = Entry("evil");
        MasterStoreIO.Save(rewritten, path);
        Choose(path, model);
        model.Collection = "Personal";
        Assert.True(model.Restore());
        Assert.Equal("+ notes (on)\n\n“Personal” no longer exists, so it is created again.", Assert.Single(h.Dialogs.Confirms).Informative);
        Assert.Equal(personal, state.Store.Collections["Personal"]);
        Assert.Equal("Default", state.ActiveCollection);   // the active collection does not change
        Assert.Null(model.RestoreError);
        Assert.Equal(1, closed);
    }

    /// <summary>A refusal is the dialog's alone on either side of the confirmation: nothing was restored, so the
    /// banner has nothing to say, and the rows then say how the collection stands.</summary>
    [Fact]
    public void ARefusalIsTheSheetsAlone()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = StagedBackup(h, state);
        var data = File.ReadAllBytes(path);
        var model = new CollectionRestoreModel(state, h.Dialogs);
        model.Load();
        Choose(path, model);
        model.Collection = "Personal";
        var banner = state.LastError;
        var closed = 0;
        model.CloseRequested += () => closed++;

        // The collection goes back as it was while the confirmation is up.
        h.Dialogs.DuringConfirm = () => state.RestoreCollection("Personal", path, data);
        Assert.False(model.Restore());
        Assert.Single(h.Dialogs.Confirms);
        Assert.Equal(AppState.RestoreCollectionUnchangedError("Personal"), model.RestoreError);
        Assert.Equal(banner, state.LastError);   // a refusal is the dialog's alone
        Assert.Equal(CollectionRestoreModel.RowState.Same, model.Rows.First(r => r.Name == "Personal").State);
        Assert.False(model.CanRestore);

        // And before the confirmation: the store changed after the rows were read.
        h.Dialogs.DuringConfirm = null;
        model.Collection = "Gone";
        Assert.True(model.CanRestore);
        state.RestoreCollection("Gone", path, data);
        Assert.False(model.Restore());
        Assert.Single(h.Dialogs.Confirms);
        Assert.Equal(AppState.RestoreCollectionUnchangedError("Gone"), model.RestoreError);
        Assert.Equal(banner, state.LastError);
        Assert.Equal(0, closed);
    }

    /// <summary>A backup that cannot be read is listed, and chosen says so on the failure line, with no collections;
    /// choosing another clears it.</summary>
    [Fact]
    public void AnUnreadableBackupSaysSoAndListsNoCollections()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var readable = Backup(h, state.Store);
        var unreadable = Path.Combine(h.BackupsDir, "mcps.2026-01-02T00-00-00-000Z.json");
        File.WriteAllText(unreadable, "{not json");
        var model = new CollectionRestoreModel(state, h.Dialogs);
        model.Load();
        Assert.Equal([Path.GetFileName(unreadable), Path.GetFileName(readable)], model.Backups.Select(b => Path.GetFileName(b.Path)));
        Assert.Null(model.RestoreError);   // nothing is said until it is chosen

        Choose(unreadable, model);
        Assert.Equal(AppState.CollectionBackupUnreadableError(Path.GetFileName(unreadable)), model.RestoreError);
        Assert.True(model.HasRestoreError);
        Assert.Empty(model.Rows);
        Assert.False(model.CanRestore);
        Assert.False(model.Restore());
        Assert.Empty(h.Dialogs.Confirms);
        Assert.Equal(AppState.CollectionBackupUnreadableError(Path.GetFileName(unreadable)), model.RestoreError);   // still said: nothing was asked

        Choose(readable, model);
        Assert.Null(model.RestoreError);
        Assert.Equal(["Default"], model.Rows.Select(r => r.Name));
    }

    [Fact]
    public void CancelClosesWithoutRestoring()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var model = new CollectionRestoreModel(state, h.Dialogs);
        var closed = 0;
        model.CloseRequested += () => closed++;
        model.Cancel();
        Assert.Equal(1, closed);
        Assert.Empty(h.Dialogs.Confirms);
    }
}

using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// Tests/ConnectorControlStateTests/ImportModelTests.swift. The Import sheet: what the rows say
/// about one document against the collection it would land in, what the count follows, and what a
/// document this app cannot read leaves on screen.
/// </summary>
public class ImportModelTests
{
    private static void Write(CollectionDocument doc, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, doc.Serialize());
    }

    [Fact]
    public void RowsShowCollisionsAndTheCountFollowsChoices()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = Path.Combine(h.Dir.File("shared"), "data-team.json");
        Write(CollectionDocumentSamples.DataTeam, path);
        // One of the document's four connectors is already in the target, under the same name.
        Assert.Null(state.Upsert("github", new McpEntry(true, AppStateHarness.Remote("https://x/")), null));

        var model = new ImportModel(state, path);
        Assert.Null(model.LoadError);
        Assert.Equal(ImportModel.Mode.AddToCollection, model.ImportMode);
        // The active collection is local, so it is the target.
        Assert.Equal("Default", model.TargetCollection);
        Assert.Equal(["Default"], model.LocalCollections);
        Assert.Equal(ImportModel.SourceLine("Data team", "Acme Data Platform", 4), model.SourceSentence);
        Assert.Equal(["dbt", "github", "ledger", "notion"], model.Rows.Select(r => r.Name));
        Assert.Equal([false, true, false, false], model.Rows.Select(r => r.Present));
        // What is already there is not imported by default.
        Assert.Equal([true, false, true, true], model.Rows.Select(r => r.Include));
        Assert.Equal([ImportChoice.Add, ImportChoice.Replace, ImportChoice.Add, ImportChoice.Add],
            model.Rows.Select(r => r.Choice));
        Assert.Equal(["server_path"], model.Rows.Single(r => r.Name == "ledger").Needs);
        // Nothing in the sample trips the cmd guard.
        Assert.All(model.Rows, row => Assert.Null(row.ExcludedReason));
        Assert.Equal(3, model.ImportCount);
        Assert.True(model.CanImport);

        // Ticking the collision in imports it too; the count follows the ticks, not the document.
        model.Rows[1].Include = true;
        Assert.Equal(4, model.ImportCount);
        model.Rows[0].Include = false;
        Assert.Equal(3, model.ImportCount);

        model.Rows[1].Choice = ImportChoice.KeepBoth;
        Assert.Null(model.Perform());
        var mcps = state.Store.Collections["Default"].Mcps;
        Assert.False(mcps.ContainsKey("dbt"), "an unticked row is skipped");
        Assert.True(mcps.ContainsKey("github 2"), "Keep both lands beside the connector that was there");
        // The one that was there is untouched.
        Assert.Equal(AppStateHarness.Remote("https://x/"), mcps["github"].Config);
        Assert.Equal("Data team", state.CollectionsFile.Collections["Default"].Provenance["ledger"].From);
        Assert.Equal(CollectionKind.Local, state.KindOf("Default"));
    }

    [Fact]
    public void SyncModeDefaultsTheNameAndSuffixesATakenOne()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = Path.Combine(h.Dir.File("shared"), "data-team.json");
        Write(CollectionDocumentSamples.DataTeam, path);

        var first = new ImportModel(state, path);
        Assert.Equal("Data team", first.SyncName);   // the document's own name is the default
        first.ImportMode = ImportModel.Mode.KeepInSync;
        // Sync mode takes every connector this platform can carry.
        Assert.Equal(4, first.ImportCount);
        Assert.True(first.CanImport);
        Assert.Null(first.Perform());
        Assert.Equal(CollectionKind.Synced, state.KindOf("Data team"));
        Assert.Equal(path, state.SourceBinding("Data team")?.Path);

        var second = new ImportModel(state, path);
        Assert.Equal("Data team 2", second.SyncName);   // a name already taken is suffixed
        second.ImportMode = ImportModel.Mode.KeepInSync;
        second.SyncName = "  ";
        Assert.False(second.CanImport, "a collection has to be called something");
    }

    [Fact]
    public void ANewerDocumentIsALoadError()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var newer = CollectionDocumentSamples.DataTeam.Encode()
            .Replacing(JsonPointer.Parse("/connectorControlCollection")!, JsonValue.Int(2))!;
        var path = h.Dir.File("future.json");
        File.WriteAllBytes(path, newer.Serialize());

        var model = new ImportModel(state, path);
        Assert.Equal(AppState.NewerDocumentError, model.LoadError);
        Assert.Empty(model.Rows);
        Assert.Equal(0, model.ImportCount);
        Assert.False(model.CanImport);
        // The sheet's button says what the sheet says.
        Assert.Equal(AppState.NewerDocumentError, model.Perform());

        var half = h.Dir.File("half.json");
        File.WriteAllText(half, "{half");
        var malformed = new ImportModel(state, half);
        Assert.StartsWith("half.json couldn’t be read: ", malformed.LoadError, StringComparison.Ordinal);
        Assert.False(malformed.CanImport);
        // A document that cannot be read creates nothing.
        Assert.Equal(["Default"], state.CollectionNames);
    }
}

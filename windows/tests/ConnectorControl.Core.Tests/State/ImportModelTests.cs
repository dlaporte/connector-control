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
        // An unticked collision shows its badge.
        Assert.Equal([false, false, false, false], model.Rows.Select(r => r.ShowsPicker));
        var raised = new List<string?>();
        model.Rows[1].PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        model.Rows[1].Include = true;
        Assert.True(model.Rows[1].ShowsPicker);   // a collision coming across asks what to do
        Assert.Contains(nameof(ImportModel.Row.ShowsPicker), raised);
        model.Rows[1].Include = false;
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

    /// <summary>
    /// Two remote connectors, one with a header name carrying the &amp; the Windows cmd /c
    /// launcher cannot hand to cmd.exe.
    /// </summary>
    private static CollectionDocument RiskyHeaderDocument() => new(
        "Risky", "Acme", "o-risky", "2026-09-21T14:02:11Z",
        new Dictionary<string, CollectionDocument.Connector>
        {
            ["bad"] = new(new CollectionDocument.Launcher.Remote(
                "https://h/mcp", new CollectionDocument.Auth.Header("X&Y"), "mcp-remote", [])),
            ["good"] = new(new CollectionDocument.Launcher.Remote(
                "https://h/mcp", CollectionDocument.Auth.Auto, "mcp-remote", [])),
        });

    /// <summary>
    /// The platform-forced half of a mirrored pair: only this build writes the cmd /c launcher,
    /// so the Mac mirror asserts the same document excludes nothing and every row counts.
    /// </summary>
    [Fact]
    public void AnExcludedRowShowsItsReasonStaysOutOfTheCountAndIsSkipped()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = Path.Combine(h.Dir.File("shared"), "risky.json");
        Write(RiskyHeaderDocument(), path);

        var model = new ImportModel(state, path);
        Assert.Equal(["bad", "good"], model.Rows.Select(r => r.Name));
        var bad = model.Rows[0];
        Assert.Equal(RemotePattern.CmdUnsafeReason(RemoteField.HeaderName), bad.ExcludedReason);
        Assert.Equal("skipped: " + RemotePattern.CmdUnsafeReason(RemoteField.HeaderName),
            ImportModel.SkippedBadge(bad.ExcludedReason!));
        // The row carries that text itself, so the template needs no converter over the factory.
        Assert.Equal(ImportModel.SkippedBadge(bad.ExcludedReason!), bad.Badge);
        Assert.False(bad.Include);
        Assert.Null(model.Rows[1].ExcludedReason);
        Assert.True(model.Rows[1].Include);

        // Out of the count in both modes: this machine has no way to run it either way.
        Assert.Equal(1, model.ImportCount);
        model.ImportMode = ImportModel.Mode.KeepInSync;
        Assert.Equal(1, model.ImportCount);
        model.ImportMode = ImportModel.Mode.AddToCollection;

        // Ticking it makes no difference — an excluded row cannot be included.
        bad.Include = true;
        Assert.Equal(1, model.ImportCount);
        Assert.Null(model.Perform());
        var mcps = state.Store.Collections["Default"].Mcps;
        Assert.True(mcps.ContainsKey("good"));
        Assert.False(mcps.ContainsKey("bad"));
        Assert.False(state.CollectionsFile.Collections["Default"].Provenance.ContainsKey("bad"));
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

    [Fact]
    public void EveryChoiceHasATitleAndACollisionOffersThree()
    {
        Assert.Equal("Add", ImportModel.ChoiceTitle(ImportChoice.Add));
        Assert.Equal("Replace", ImportModel.ChoiceTitle(ImportChoice.Replace));
        Assert.Equal("Keep both", ImportModel.ChoiceTitle(ImportChoice.KeepBoth));
        Assert.Equal("Skip", ImportModel.ChoiceTitle(ImportChoice.Skip));
        // The picker is a collision's, so the case where nothing is in the way is not in it.
        Assert.Equal([ImportChoice.Replace, ImportChoice.KeepBoth, ImportChoice.Skip], ImportModel.CollisionChoices);
        Assert.Equal(["Replace", "Keep both", "Skip"], ImportModel.CollisionChoices.Select(ImportModel.ChoiceTitle));
    }

    [Fact]
    public void ARowsCautionIsTheSentenceAConnectorAlreadyCarries()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("data-team.json");
        Write(CollectionDocumentSamples.DataTeam, path);
        var model = new ImportModel(state, path);

        // The document's own needs, sorted, in the wording the window and the editor use.
        var dbt = model.Rows.Single(r => r.Name == "dbt");
        Assert.Equal(["DBT_TOKEN"], dbt.Needs);
        Assert.Equal(AppState.NeedsValueCaution("DBT_TOKEN"), dbt.NeedsCaution);
        var notion = model.Rows.Single(r => r.Name == "notion");
        Assert.Equal(["token"], notion.Needs);
        Assert.Equal(AppState.NeedsValueCaution("token"), notion.NeedsCaution);

        // No glyph for a connector the author left nothing to fill in.
        var github = model.Rows.Single(r => r.Name == "github");
        Assert.Empty(github.Needs);
        Assert.Null(github.NeedsCaution);

        // The same connector, once imported, says exactly the same thing in the window.
        Assert.Null(model.Perform());
        Assert.Equal(dbt.NeedsCaution, state.ConnectorCaution("dbt", "Default"));
    }
    [Fact]
    public void EachRowCarriesItsOwnBadge()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("data-team.json");
        Write(CollectionDocumentSamples.DataTeam, path);
        // One of the document's four connectors is already in the target, under the same name.
        Assert.Null(state.Upsert("github", new McpEntry(AppStateHarness.Remote("https://x/")), null));

        var model = new ImportModel(state, path);
        Assert.Equal(["dbt", "github", "ledger", "notion"], model.Rows.Select(r => r.Name));
        Assert.Equal(
            [ImportModel.NewBadge, ImportModel.PresentBadge, ImportModel.NewBadge, ImportModel.NewBadge],
            model.Rows.Select(r => r.Badge));
        // The badge is what the row is, not what the user has since ticked.
        model.Rows[1].Include = true;
        model.Rows[1].Choice = ImportChoice.Replace;
        Assert.Equal(ImportModel.PresentBadge, model.Rows[1].Badge);
    }
    /// <summary>
    /// Two needs in one connector, one in an argument and one in an environment value, so first
    /// appearance and alphabetical order disagree: object keys are walked sorted, and Args comes
    /// before Env, while the names sort the other way.
    /// </summary>
    private static CollectionDocument TwoNeedsDocument() => new(
        "Two", "Acme", "o-two", "2026-09-21T14:02:11Z",
        new Dictionary<string, CollectionDocument.Connector>
        {
            ["pair"] = new(
                new CollectionDocument.Launcher.Local("node", ["${CC_NEEDS:zulu}"], CollectionPlatform.Mac),
                env: [new("ALPHA", new CollectionDocument.EnvValue.Hint("the alpha hint"))],
                needs: [new("zulu", "the zulu hint")]),
        });

    [Fact]
    public void ARowsNeedsFollowTheConfigRatherThanTheAlphabet()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("two.json");
        Write(TwoNeedsDocument(), path);
        var model = new ImportModel(state, path);

        var pair = model.Rows.Single(r => r.Name == "pair");
        // First appearance in the config, not sorted.
        Assert.Equal(["zulu", "ALPHA"], pair.Needs);
        Assert.Equal(AppState.NeedsValueCaution("zulu, ALPHA"), pair.NeedsCaution);

        // Once imported, the connector's own caution is the very same sentence.
        Assert.Null(model.Perform());
        Assert.Equal(pair.NeedsCaution, state.ConnectorCaution("pair", "Default"));
    }
    [Fact]
    public void ATickedRowSetToSkipIsNotCounted()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = Path.Combine(h.Dir.File("shared"), "data-team.json");
        Write(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Upsert("github", new McpEntry(AppStateHarness.Remote("https://x/")), null));

        var model = new ImportModel(state, path);
        Assert.Equal(3, model.ImportCount);   // the collision starts unticked

        // Ticked and set to Skip: the button must not promise what Perform will not land.
        model.Rows[1].Include = true;
        model.Rows[1].Choice = ImportChoice.Skip;
        Assert.Equal(3, model.ImportCount);
        model.Rows[1].Choice = ImportChoice.Replace;
        Assert.Equal(4, model.ImportCount);

        // Every row skipped is nothing to import at all.
        foreach (var row in model.Rows)
        {
            row.Choice = ImportChoice.Skip;
        }
        Assert.Equal(0, model.ImportCount);
        Assert.False(model.CanImport);
        // Sync mode takes the whole document, so a per-row choice says nothing about it.
        model.ImportMode = ImportModel.Mode.KeepInSync;
        Assert.Equal(4, model.ImportCount);
    }

    /// <summary>
    /// C#-only: SwiftUI binds the optional <c>loadError</c> itself, so the Mac has no bool to
    /// assert — the same one-sided pattern <c>HasCollectionBanner</c> already follows.
    /// </summary>
    [Fact]
    public void ADocumentThatCannotBeReadSaysSoAsABool()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var good = Path.Combine(h.Dir.File("shared"), "data-team.json");
        Write(CollectionDocumentSamples.DataTeam, good);
        Assert.False(new ImportModel(state, good).HasLoadError);

        var half = h.Dir.File("half.json");
        File.WriteAllText(half, "{half");
        var malformed = new ImportModel(state, half);
        Assert.True(malformed.HasLoadError);
        Assert.NotNull(malformed.LoadError);
    }

    [Fact]
    public void TheAccessibilityLabelsNameTheirControls()
    {
        Assert.Equal("Include github", ImportModel.IncludeLabel("github"));
        Assert.Equal("Collection name", ImportModel.SyncNameLabel);
        Assert.Equal("What to do with github", ImportModel.CollisionPickerLabel("github"));
    }
    /// <summary>
    /// C#-only: the Mac's rows are structs in a @Published array, so editing one republishes the
    /// array and nothing model-side needs to raise.
    /// </summary>
    [Fact]
    public void TickingOrSkippingARowRaisesTheCountWithoutAViewCall()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = Path.Combine(h.Dir.File("shared"), "data-team.json");
        Write(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Upsert("github", new McpEntry(AppStateHarness.Remote("https://x/")), null));

        var model = new ImportModel(state, path);
        var raised = new List<string>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
        Assert.Equal(3, model.ImportCount);

        // A choice the sheet makes on a row has to reach the footer, which reads the model.
        model.Rows[1].Include = true;
        Assert.Contains(nameof(ImportModel.ImportCount), raised);
        Assert.Contains(nameof(ImportModel.CanImport), raised);
        Assert.Equal(4, model.ImportCount);

        raised.Clear();
        model.Rows[1].Choice = ImportChoice.Skip;
        Assert.Contains(nameof(ImportModel.ImportCount), raised);
        Assert.Equal(3, model.ImportCount);

        // Setting a row to what it already holds says nothing.
        raised.Clear();
        model.Rows[1].Choice = ImportChoice.Skip;
        Assert.Empty(raised);

        // Rebuilding the rows lets the old ones go: the replaced row no longer reaches the model.
        var stale = model.Rows[1];
        Assert.Null(state.CreateCollection("Other"));
        model.TargetCollection = "Other";
        raised.Clear();
        stale.Include = !stale.Include;
        Assert.Empty(raised);
    }

    /// <summary>
    /// C#-only, as the test above is: on the Mac the mode, the target and the name are @Published,
    /// so changing one republishes the model. Here the Import button's gate and count follow each
    /// of them, and the dialog must not have to nudge its own binding to see that.
    /// </summary>
    [Fact]
    public void TheModeTheTargetAndTheNameRaiseTheGateThatFollowsThem()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = Path.Combine(h.Dir.File("shared"), "data-team.json");
        Write(CollectionDocumentSamples.DataTeam, path);
        var model = new ImportModel(state, path);
        var raised = new List<string>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        model.ImportMode = ImportModel.Mode.KeepInSync;
        Assert.Contains(nameof(ImportModel.CanImport), raised);
        Assert.Contains(nameof(ImportModel.ImportCount), raised);

        raised.Clear();
        model.SyncName = "  ";
        Assert.Contains(nameof(ImportModel.CanImport), raised);
        Assert.False(model.CanImport);

        raised.Clear();
        model.ImportMode = ImportModel.Mode.AddToCollection;
        Assert.Null(state.CreateCollection("Other"));
        raised.Clear();
        model.TargetCollection = "Other";
        Assert.Contains(nameof(ImportModel.CanImport), raised);
        Assert.Contains(nameof(ImportModel.ImportCount), raised);

        // Setting what is already there says nothing.
        raised.Clear();
        model.ImportMode = ImportModel.Mode.AddToCollection;
        model.SyncName = "  ";
        Assert.Empty(raised);
    }
}

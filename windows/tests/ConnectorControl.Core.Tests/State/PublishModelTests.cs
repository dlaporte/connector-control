using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// Tests/ConnectorControlStateTests/PublishModelTests.swift. The Publish/Export sheet: which rows
/// the collection produces, what ticking them says in the intent, and what the preview and the
/// warnings show for it.
/// </summary>
public class PublishModelTests
{
    /// <summary>One local connector with a stripped and a shared environment value and one
    /// absolute path argument — a row of each kind the sheet offers.</summary>
    private static JsonValue Connector() => JsonValue.Object(new Dictionary<string, JsonValue>(StringComparer.Ordinal)
    {
        ["command"] = JsonValue.String("node"),
        ["args"] = JsonValue.Array([JsonValue.String("/Users/d/x.js"), JsonValue.String("--quiet")]),
        ["env"] = JsonValue.Object(new Dictionary<string, JsonValue>(StringComparer.Ordinal)
        {
            ["A"] = JsonValue.String("sk-live-secret"),
            ["B"] = JsonValue.String("us"),
        }),
    });

    private static AppState Started(AppStateHarness h)
    {
        var state = h.Create();
        Assert.Null(state.Upsert("c", new McpEntry(true, Connector()), null));
        return state;
    }

    private static string PublishFolder(AppStateHarness h)
    {
        var path = h.Dir.File("pub");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void RowsComeFromTheCollectionAndBuildTheIntent()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var model = new PublishModel(state, state.ActiveCollection);

        Assert.Equal(["A", "B"], model.EnvRows.Select(r => r.Name));
        Assert.Equal(["c", "c"], model.EnvRows.Select(r => r.Connector));
        Assert.All(model.EnvRows, row => Assert.False(row.Share));   // stripped is the default
        // Only the argument that looks like a path is offered.
        var path = Assert.Single(model.PathRows);
        Assert.Equal(JsonPointer.Parse("/args/0")!, path.Pointer);
        Assert.Equal("/Users/d/x.js", path.Value);
        Assert.False(path.Marked);
        Assert.Equal("path", path.Name);
        Assert.Equal(Slug.Make(state.ActiveCollection) + ".json", model.FileName);
        Assert.False(model.CanPublish);   // nothing is published until a folder is chosen
        Assert.Equal(PublishIntent.None, model.Intent);

        model.EnvRows[1].Share = true;
        model.PathRows[0].Marked = true;
        model.PathRows[0].Name = "srv";
        model.PathRows[0].Hint = "your ledger clone, then dist/index.js";
        Assert.Equal(["B"], model.Intent.ShareValues["c"].Order(StringComparer.Ordinal));
        // The mark records the path it was made on, so it can find it again once arguments move.
        Assert.Equal(new PublishIntent.PathMark("srv", "your ledger clone, then dist/index.js", "/Users/d/x.js"),
            model.Intent.PathMarks["c"][JsonPointer.Parse("/args/0")!]);
        // A marked path leaves as its placeholder.
        Assert.Contains("${CC_NEEDS:srv}", model.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void EachConnectorNumbersItsOwnPathsAndOnlyLocalOnesOfferAny()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        Assert.Null(state.Upsert("two", new McpEntry(true, JsonValue.Object(
            new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            {
                ["command"] = JsonValue.String("node"),
                ["args"] = JsonValue.Array([
                    JsonValue.String("~/one.js"), JsonValue.String("./two.js"), JsonValue.String("https://example.com/x"),
                ]),
            })), null));
        var model = new PublishModel(state, state.ActiveCollection);
        Assert.Equal(["path", "path_2"], model.PathRows.Where(r => r.Connector == "two").Select(r => r.Name));
        // The numbering starts again in every connector.
        Assert.Equal(["path"], model.PathRows.Where(r => r.Connector == "c").Select(r => r.Name));
        // The three connectors the harness seeds are remote: their arguments are the launcher's,
        // not the author's, and marking them would say nothing.
        Assert.Equal(["c", "two"], model.PathRows.Select(r => r.Connector).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PreviewHidesUnsharedValuesAndListsWarnings()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var model = new PublishModel(state, state.ActiveCollection);
        Assert.False(JsonText.Contains(model.Preview, "sk-live-secret"));   // the default is stripped
        // A value that never leaves this machine is nothing to warn about.
        Assert.Empty(model.Warnings);

        model.EnvRows[0].Hint = "the ledger dashboard ▸ API tokens";
        Assert.Contains("the ledger dashboard ▸ API tokens", model.Preview, StringComparison.Ordinal);

        model.EnvRows[0].Share = true;
        // The preview shows every byte that leaves.
        Assert.Contains("sk-live-secret", model.Preview, StringComparison.Ordinal);
        Assert.Equal([PublishModel.WarningLine("c", "env.A looks like a credential")], model.Warnings);
    }

    [Fact]
    public void PublishingThroughTheSheetRecordsWhatWasTickedAndReopensWithIt()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var folder = PublishFolder(h);
        var model = new PublishModel(state, state.ActiveCollection) { Folder = folder };
        Assert.True(model.CanPublish);
        model.EnvRows[1].Share = true;
        model.PathRows[0].Marked = true;
        model.PathRows[0].Name = "srv";
        Assert.Null(model.Publish());

        var document = CollectionDocument.Decode(File.ReadAllBytes(Path.Combine(folder, model.FileName)));
        Assert.Equal(new CollectionDocument.EnvValue.Value("us"), document.Connectors["c"].Env["B"]);
        Assert.True(document.Connectors["c"].Needs.ContainsKey("srv"));
        Assert.Null(document.Connectors["c"].Needs["srv"]);

        var reopened = new PublishModel(state, state.ActiveCollection);
        Assert.Equal(folder, reopened.Folder);
        Assert.True(reopened.EnvRows.Single(r => r.Name == "B").Share);
        Assert.True(reopened.PathRows[0].Marked);
        Assert.Equal("srv", reopened.PathRows[0].Name);

        // The file name is fixed when publishing starts, so a rename never orphans the document
        // the team already subscribed to.
        Assert.Null(state.RenameCollection(state.ActiveCollection, "Team"));
        Assert.Equal("default.json", new PublishModel(state, "Team").FileName);
    }

    [Fact]
    public void AReopenedSheetTicksAPathWhereItNowStands()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var model = new PublishModel(state, state.ActiveCollection) { Folder = PublishFolder(h) };
        model.PathRows[0].Marked = true;
        model.PathRows[0].Name = "srv";
        Assert.Null(model.Publish());

        // Moved outside the editor, so the record still says where it was.
        Assert.Null(state.Upsert("c", new McpEntry(true, JsonValue.Object(
            ("command", JsonValue.String("node")),
            ("args", JsonValue.Array([JsonValue.String("--quiet"), JsonValue.String("/Users/d/x.js")])))), "c"));
        var reopened = new PublishModel(state, state.ActiveCollection);
        var row = reopened.PathRows.First(r => r.Connector == "c");
        Assert.Equal(new JsonPointer(["args", "1"]), row.Pointer);
        Assert.True(row.Marked, "the tick sits where the exporter places the mark");
        Assert.Equal("srv", row.Name);

        // A marked argument that does not look like a path keeps its row, so publishing from the
        // dialog cannot quietly unmark it. Recorded as the author's reviewed answer, which is what
        // lets the path it replaces leave this machine's list of marked paths.
        var flag = new PublishIntent(
            [],
            [new("c", new Dictionary<JsonPointer, PublishIntent.PathMark>
            {
                [new JsonPointer(["args", "0"])] = new("flag", null, "--quiet"),
            })],
            []);
        Assert.Null(state.UpdatePublishIntent(state.ActiveCollection, flag, new HashSet<string>(["--quiet"], StringComparer.Ordinal)));
        var marked = new PublishModel(state, state.ActiveCollection);
        Assert.Equal(["--quiet"], marked.PathRows.Where(r => r.Connector == "c" && r.Marked).Select(r => r.Value));
        Assert.Equal(flag, marked.Intent);
    }

    // MARK: a mark that lost its argument

    private static JsonValue Node(params string[] args) => JsonValue.Object(
        ("command", JsonValue.String("node")),
        ("args", JsonValue.Array(args.Select(JsonValue.String))));

    private static IReadOnlyList<string> DocumentArgs(string connector, byte[] data) =>
        Assert.IsType<CollectionDocument.Launcher.Local>(CollectionDocument.Decode(data).Connectors[connector].Launcher).Args;

    private static IReadOnlyDictionary<JsonPointer, PublishIntent.PathMark>? RecordedMarks(AppState state, string connector) =>
        state.CollectionsFile.Collections.GetValueOrDefault(state.ActiveCollection)?.Publish?.Intent.PathMarks
            .GetValueOrDefault(connector);

    /// <summary>
    /// "c" published through the dialog with its path marked "srv", then that path edited outside
    /// the editor, so the record's mark has lost its argument. Returns the document's path.
    /// </summary>
    private static string PublishThenLoseTheMark(AppStateHarness h, AppState state)
    {
        var first = new PublishModel(state, state.ActiveCollection) { Folder = PublishFolder(h) };
        first.PathRows[0].Marked = true;
        first.PathRows[0].Name = "srv";
        Assert.Null(first.Publish());
        Assert.Null(state.Upsert("c", new McpEntry(Node("/Users/d/y.js", "--quiet")), "c"));
        Assert.Equal(AppState.PathMarkMovedError("c"), state.PublishError?.Message);
        return Path.Combine(PublishFolder(h), first.FileName);
    }

    [Fact]
    public void AReopenedSheetKeepsALostMarkAndWritesNothingUntilItIsAnswered()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var file = PublishThenLoseTheMark(h, state);
        var before = File.ReadAllBytes(file);

        var sheet = new PublishModel(state, state.ActiveCollection);
        Assert.Equal(["c"], sheet.UnresolvedMarks.Select(m => m.Connector));
        // The path it lost is not where it was.
        Assert.False(sheet.PathRows.First(r => r.Connector == "c").Marked);
        Assert.NotNull(sheet.Folder);
        // A folder is on record, and still nothing can be published.
        Assert.False(sheet.CanPublish);
        Assert.False(sheet.CanExport);

        var output = h.Dir.File(Path.Combine("away", "copy.json"));
        Assert.Equal(PublishModel.UnresolvedMarkNote("c", "srv"), sheet.Export(output));
        Assert.False(File.Exists(output));
        Assert.Equal(PublishModel.UnresolvedMarkNote("c", "srv"), sheet.Publish());
        Assert.Equal(before, File.ReadAllBytes(file));
        // The record is left as it was.
        Assert.Equal("/Users/d/x.js", Assert.Single(RecordedMarks(state, "c")!).Value.Value);
    }

    [Fact]
    public void TickingThePathWhereItNowSitsAnswersTheLostMark()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var file = PublishThenLoseTheMark(h, state);
        var sheet = new PublishModel(state, state.ActiveCollection);
        var row = sheet.PathRows.Single(r => r.Connector == "c" && r.Value == "/Users/d/y.js");

        row.Marked = true;
        Assert.Empty(sheet.UnresolvedMarks);
        Assert.True(sheet.CanPublish);
        Assert.True(sheet.CanExport);
        // A tick with no name to carry would publish the path, so it answers nothing.
        row.Name = "  ";
        Assert.Equal(["c"], sheet.UnresolvedMarks.Select(m => m.Connector));
        // Unticking puts it back.
        row.Marked = false;
        row.Name = "srv";
        Assert.Equal(["c"], sheet.UnresolvedMarks.Select(m => m.Connector));

        row.Marked = true;
        Assert.Null(sheet.Publish());
        Assert.Null(state.PublishError);
        Assert.Equal(["${CC_NEEDS:srv}", "--quiet"], DocumentArgs("c", File.ReadAllBytes(file)));
        // The tick replaced the lost mark, pointer and value both.
        var mark = Assert.Single(RecordedMarks(state, "c")!);
        Assert.Equal(new JsonPointer(["args", "0"]), mark.Key);
        Assert.Equal(new PublishIntent.PathMark("srv", null, "/Users/d/y.js"), mark.Value);
    }

    [Fact]
    public void ARowTickedOnOpenAnswersNoOtherLostMark()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        Assert.Null(state.Upsert("two", new McpEntry(Node("/Users/d/one.js", "/Users/d/two.js")), null));
        var first = new PublishModel(state, state.ActiveCollection) { Folder = PublishFolder(h) };
        foreach (var row in first.PathRows.Where(r => r.Connector == "two"))
        {
            row.Marked = true;
        }
        Assert.Null(first.Publish());
        Assert.Null(state.Upsert("two", new McpEntry(Node("/Users/d/one.js", "/Users/d/2.js")), "two"));

        var sheet = new PublishModel(state, state.ActiveCollection);
        Assert.Equal(["/Users/d/one.js"], sheet.PathRows.Where(r => r.Connector == "two" && r.Marked).Select(r => r.Value));
        // The tick on one.js was already on record, so it stands in for nothing.
        Assert.Equal(["two"], sheet.UnresolvedMarks.Select(m => m.Connector));
        sheet.PathRows.Single(r => r.Value == "/Users/d/2.js").Marked = true;
        Assert.Empty(sheet.UnresolvedMarks);
    }

    [Fact]
    public void ForgettingALostMarkSendsThePathAsThePreviewShows()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var file = PublishThenLoseTheMark(h, state);
        var sheet = new PublishModel(state, state.ActiveCollection);
        // The preview shows what forgetting the mark would send.
        Assert.Equal(["/Users/d/y.js", "--quiet"], DocumentArgs("c", System.Text.Encoding.UTF8.GetBytes(sheet.Preview)));

        sheet.ForgetUnresolvedMark(sheet.UnresolvedMarks.Single(m => m.Connector == "c").Id);
        Assert.Empty(sheet.UnresolvedMarks);
        Assert.True(sheet.CanPublish);
        Assert.True(sheet.CanExport);
        Assert.Null(sheet.Publish());
        Assert.Null(state.PublishError);
        // The author's explicit choice: the path travels as written.
        Assert.Equal(["/Users/d/y.js", "--quiet"], DocumentArgs("c", File.ReadAllBytes(file)));
        Assert.Null(RecordedMarks(state, "c"));
        // Forgetting then publishing is how a path leaves this machine's list.
        Assert.Empty(state.CollectionsCache.Published[state.ActiveCollection].MarkedValues);
    }

    [Fact]
    public void ARowHoldingAPathThisMachineKeepsBackStartsTicked()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var first = new PublishModel(state, state.ActiveCollection) { Folder = PublishFolder(h) };
        first.PathRows[0].Marked = true;
        first.PathRows[0].Name = "srv";
        Assert.Null(first.Publish());
        var file = Path.Combine(PublishFolder(h), first.FileName);

        // The other machine dropped the mark; its sidecar is here, its master list is not.
        var entry = state.CollectionsFile.Collections[state.ActiveCollection];
        var record = entry.Publish!;
        new CollectionsFile(state.CollectionsFile.Collections.Select(p => p.Key == state.ActiveCollection
            ? new KeyValuePair<string, CollectionsFile.Entry>(p.Key, new CollectionsFile.Entry(
                entry.Kind, entry.FileName, entry.RelativeToStore, entry.Origin, entry.Needs,
                new CollectionsFile.PublishRecord(record.Slug, record.Origin,
                    record.Intent.ReplacingPathMarks("c", new Dictionary<JsonPointer, PublishIntent.PathMark>())),
                entry.Provenance))
            : p)).Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        state.Reload();
        Assert.Equal(AppState.KeptPathCarriedError("c", FieldName.Argument(1)), state.PublishError?.Message);

        var sheet = new PublishModel(state, state.ActiveCollection);
        // The record no longer marks it, but this machine has sent it as a placeholder.
        Assert.True(sheet.PathRows.First(r => r.Connector == "c").Marked);
        Assert.Empty(sheet.UnresolvedMarks);
        Assert.Null(sheet.Publish());
        Assert.Null(state.PublishError);
        Assert.Equal(["${CC_NEEDS:path}", "--quiet"], DocumentArgs("c", File.ReadAllBytes(file)));
    }

    [Fact]
    public void AMarkWhoseConnectorIsGoneWaitsToBeForgotten()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var first = new PublishModel(state, state.ActiveCollection) { Folder = PublishFolder(h) };
        first.PathRows[0].Marked = true;
        Assert.Null(first.Publish());
        var file = Path.Combine(PublishFolder(h), first.FileName);

        // Renamed by an older app on another machine: the master list arrives, the record does not follow.
        var store = h.StoreOnDisk();
        var mcps = store.Collections[store.ActiveCollection].Mcps;
        mcps["d"] = mcps["c"];
        mcps.Remove("c");
        MasterStoreIO.Save(store, h.MasterStorePath);
        state.Reload(ReloadTrigger.ExternalStoreAdoption);

        var sheet = new PublishModel(state, state.ActiveCollection);
        Assert.Equal(["c"], sheet.UnresolvedMarks.Select(m => m.Connector));
        sheet.PathRows.First(r => r.Connector == "d").Marked = true;
        // A tick in another connector answers nothing about this one.
        Assert.Equal(["c"], sheet.UnresolvedMarks.Select(m => m.Connector));
        Assert.False(sheet.CanPublish);
        sheet.ForgetUnresolvedMark(sheet.UnresolvedMarks.Single(m => m.Connector == "c").Id);
        Assert.True(sheet.CanPublish);
        Assert.Null(sheet.Publish());
        Assert.Null(state.PublishError);
        Assert.Equal(["${CC_NEEDS:path}", "--quiet"], DocumentArgs("d", File.ReadAllBytes(file)));
    }

    /// <summary>
    /// C#-only, for the reason <see cref="EditingARowRaisesWhatIsDerivedFromItWithoutAViewCall"/>
    /// gives: the dialog enables its buttons and shows the note from these raises alone.
    /// </summary>
    [Fact]
    public void AnsweringALostMarkRaisesTheGatesWithoutAViewCall()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        PublishThenLoseTheMark(h, state);
        var sheet = new PublishModel(state, state.ActiveCollection);
        var raised = new List<string>();
        sheet.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        sheet.PathRows.Single(r => r.Connector == "c").Marked = true;
        Assert.Contains(nameof(PublishModel.UnresolvedMarks), raised);
        Assert.Contains(nameof(PublishModel.CanPublish), raised);
        Assert.Contains(nameof(PublishModel.CanExport), raised);

        sheet.PathRows.Single(r => r.Connector == "c").Marked = false;
        var lost = Assert.Single(sheet.UnresolvedMarks);
        raised.Clear();
        sheet.ForgetUnresolvedMark(lost.Id);
        Assert.Contains(nameof(PublishModel.UnresolvedMarks), raised);
        Assert.Contains(nameof(PublishModel.KeptPaths), raised);
        Assert.Contains(nameof(PublishModel.CanPublish), raised);
        Assert.Contains(nameof(PublishModel.CanExport), raised);
        // Forgetting what is already forgotten says nothing.
        raised.Clear();
        sheet.ForgetUnresolvedMark(lost.Id);
        Assert.Empty(raised);
    }

    // MARK: each lost mark on its own

    private static PublishIntent LedgerMarks(params (int Index, PublishIntent.PathMark Mark)[] marks) => new(
        [],
        [new("ledger", marks.ToDictionary(
            m => new JsonPointer(["args", m.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)]), m => m.Mark))],
        []);

    /// <summary>
    /// "ledger" published with two marked paths, then both paths edited outside the editor, so each
    /// mark has lost its argument. Returns the document's path.
    /// </summary>
    private static string PublishTwoMarksThenLoseBoth(AppStateHarness h, AppState state)
    {
        Assert.Null(state.Upsert("ledger", new McpEntry(Node("/Users/d/a/one.js", "/Users/d/b/two.js")), null));
        var folder = PublishFolder(h);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, LedgerMarks(
            (0, new PublishIntent.PathMark("first", "h1", "/Users/d/a/one.js")),
            (1, new PublishIntent.PathMark("second", "h2", "/Users/d/b/two.js"))),
            new HashSet<string>(["/Users/d/a/one.js", "/Users/d/b/two.js"], StringComparer.Ordinal)));
        Assert.Null(state.Upsert("ledger", new McpEntry(Node("/Users/d/a2/one.js", "/Users/d/b2/two.js")), "ledger"));
        return Path.Combine(folder, Slug.Make(state.ActiveCollection) + ".json");
    }

    [Fact]
    public void EachLostMarkIsAnsweredByATickOfItsOwn()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var file = PublishTwoMarksThenLoseBoth(h, state);
        var before = File.ReadAllBytes(file);
        var sheet = new PublishModel(state, state.ActiveCollection);
        Assert.Equal(["ledger first", "ledger second"], sheet.UnresolvedMarks.Select(m => $"{m.Connector} {m.Name}"));
        var one = sheet.PathRows.Single(r => r.Value == "/Users/d/a2/one.js");
        var two = sheet.PathRows.Single(r => r.Value == "/Users/d/b2/two.js");

        one.Marked = true;
        // One tick answers one mark.
        Assert.Equal(["second"], sheet.UnresolvedMarks.Select(m => m.Name));
        Assert.Equal(("first", "h1"), (one.Name, one.Hint));
        Assert.False(sheet.CanPublish);
        Assert.Equal(PublishModel.UnresolvedMarkNote("ledger", "second"), sheet.Publish());
        // The second moved path does not travel.
        Assert.Equal(before, File.ReadAllBytes(file));

        two.Marked = true;
        Assert.Empty(sheet.UnresolvedMarks);
        // Each tick carries the name of the mark it answers.
        Assert.Equal(("second", "h2"), (two.Name, two.Hint));
        Assert.Null(sheet.Publish());
        Assert.Equal(["${CC_NEEDS:first}", "${CC_NEEDS:second}"], DocumentArgs("ledger", File.ReadAllBytes(file)));
        var recorded = RecordedMarks(state, "ledger")!;
        Assert.Equal(new PublishIntent.PathMark("first", "h1", "/Users/d/a2/one.js"), recorded[new JsonPointer(["args", "0"])]);
        Assert.Equal(new PublishIntent.PathMark("second", "h2", "/Users/d/b2/two.js"), recorded[new JsonPointer(["args", "1"])]);
    }

    [Fact]
    public void UntickingPutsBackTheMarkItAnsweredAndEachMarkIsForgottenAlone()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        PublishTwoMarksThenLoseBoth(h, state);
        var sheet = new PublishModel(state, state.ActiveCollection);
        var one = sheet.PathRows.Single(r => r.Value == "/Users/d/a2/one.js");
        var two = sheet.PathRows.Single(r => r.Value == "/Users/d/b2/two.js");
        one.Marked = true;
        two.Marked = true;
        one.Marked = false;
        // Unticking gives back the mark that row answered.
        Assert.Equal(["first"], sheet.UnresolvedMarks.Select(m => m.Name));

        var first = sheet.UnresolvedMarks[0];
        two.Marked = false;
        Assert.Equal(["first", "second"], sheet.UnresolvedMarks.Select(m => m.Name));
        sheet.ForgetUnresolvedMark(first.Id);
        // Forget Mark forgets that one mark.
        Assert.Equal(["second"], sheet.UnresolvedMarks.Select(m => m.Name));
        Assert.False(sheet.CanPublish);
    }

    // MARK: a kept value outside the argument rows

    private const string KeptPathValue = "/Users/d/ledger/dist/index.js";

    /// <summary>
    /// "ledger" published with its path marked; then the other machine's save drops the mark and
    /// leaves the path only in an <c>additional</c> field no row offers.
    /// </summary>
    private static string PublishThenMoveThePathOutOfTheRows(AppStateHarness h, AppState state)
    {
        Assert.Null(state.Upsert("ledger", new McpEntry(Node(KeptPathValue)), null));
        var folder = PublishFolder(h);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder,
            LedgerMarks((0, new PublishIntent.PathMark("server_path", null, KeptPathValue))),
            new HashSet<string>([KeptPathValue], StringComparer.Ordinal)));
        var entry = state.CollectionsFile.Collections[state.ActiveCollection];
        var record = entry.Publish!;
        new CollectionsFile(state.CollectionsFile.Collections.Select(p => p.Key == state.ActiveCollection
            ? new KeyValuePair<string, CollectionsFile.Entry>(p.Key, new CollectionsFile.Entry(
                entry.Kind, entry.FileName, entry.RelativeToStore, entry.Origin, entry.Needs,
                new CollectionsFile.PublishRecord(record.Slug, record.Origin,
                    record.Intent.ReplacingPathMarks("ledger", new Dictionary<JsonPointer, PublishIntent.PathMark>())),
                entry.Provenance))
            : p)).Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        var store = h.StoreOnDisk();
        var mcps = store.Collections[store.ActiveCollection].Mcps;
        mcps["ledger"] = mcps["ledger"] with
        {
            Config = JsonValue.Object(
                ("command", JsonValue.String("node")),
                ("args", JsonValue.Array([JsonValue.String("--serve")])),
                ("cwd", JsonValue.String(KeptPathValue))),
        };
        MasterStoreIO.Save(store, h.MasterStorePath);
        state.Reload(ReloadTrigger.ExternalStoreAdoption);
        Assert.Equal(AppState.KeptPathCarriedError("ledger", FieldName.Document("additional.cwd")), state.PublishError?.Message);
        return Path.Combine(folder, Slug.Make(state.ActiveCollection) + ".json");
    }

    [Fact]
    public void AKeptValueOutsideTheRowsIsListedAndHoldsPublishUntilReleased()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var file = PublishThenMoveThePathOutOfTheRows(h, state);
        var before = File.ReadAllBytes(file);
        var sheet = new PublishModel(state, state.ActiveCollection);
        Assert.Empty(sheet.UnresolvedMarks);
        Assert.Equal([new PublishModel.KeptPath(KeptPathValue, "ledger", "additional.cwd")], sheet.KeptPaths);
        Assert.False(sheet.CanPublish);
        Assert.False(sheet.CanExport);
        Assert.Equal(PublishModel.KeptPathNote("ledger", FieldName.Document("additional.cwd")), sheet.Publish());
        var output = h.Dir.File(Path.Combine("away", "copy.json"));
        Assert.Equal(PublishModel.KeptPathNote("ledger", FieldName.Document("additional.cwd")), sheet.Export(output));
        Assert.False(File.Exists(output));
        Assert.Equal(before, File.ReadAllBytes(file));
        // Nothing released it.
        Assert.Equal([KeptPathValue], state.CollectionsCache.Published[state.ActiveCollection].MarkedValues);

        sheet.ReleaseKeptPath(KeptPathValue);
        Assert.Empty(sheet.KeptPaths);
        Assert.True(sheet.CanPublish);
        Assert.Null(sheet.Publish());
        Assert.Null(state.PublishError);
        // The author's explicit choice: the path travels as written.
        Assert.True(JsonText.Contains(File.ReadAllBytes(file), KeptPathValue));
        var binding = state.CollectionsCache.Published[state.ActiveCollection];
        Assert.Empty(binding.MarkedValues);
        Assert.Equal([KeptPathValue], binding.ReleasedValues);
        state.Reload();
        // A released path stays released.
        Assert.Null(state.PublishError);
    }

    [Fact]
    public void AHintHoldingAMarkedPathIsListedWhereItSits()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.Upsert("ledger", new McpEntry(Node(KeptPathValue)), null));
        var sheet = new PublishModel(state, state.ActiveCollection) { Folder = PublishFolder(h) };
        sheet.PathRows[0].Marked = true;
        sheet.PathRows[0].Name = "server_path";
        sheet.PathRows[0].Hint = $"like mine, {KeptPathValue}";
        Assert.Equal(["ledger needs.server_path.hint"], sheet.KeptPaths.Select(k => $"{k.Connector} {k.Field}"));
        Assert.False(sheet.CanPublish);
        Assert.Equal(PublishModel.KeptPathNote("ledger", FieldName.Hint("server_path")), sheet.Publish());
        Assert.False(File.Exists(Path.Combine(PublishFolder(h), sheet.FileName)));
        sheet.PathRows[0].Hint = "your ledger clone";
        Assert.True(sheet.CanPublish);
        Assert.Null(sheet.Publish());
    }

    /// <summary>
    /// A filesystem server started on "." and a remote connector beside it: a relative value counts
    /// only where a string is exactly it, so the dots in a URL are not the marked path.
    /// </summary>
    [Fact]
    public void AShortRelativeMarkHoldsBackOnlyItself()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.Upsert("files", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String("npx")),
            ("args", JsonValue.Array([JsonValue.String("-y"), JsonValue.String("@modelcontextprotocol/server-filesystem"), JsonValue.String(".")])))),
            null));
        Assert.Null(state.Upsert("notion", new McpEntry(AppStateHarness.Remote("https://mcp.notion.com/mcp")), null));
        var sheet = new PublishModel(state, state.ActiveCollection) { Folder = PublishFolder(h) };
        sheet.PathRows.Single(r => r.Value == ".").Marked = true;
        Assert.Empty(sheet.KeptPaths);
        Assert.True(sheet.CanPublish);
        Assert.Null(sheet.Publish());
        Assert.Null(state.PublishError);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "${CC_NEEDS:path}"],
                     DocumentArgs("files", File.ReadAllBytes(Path.Combine(PublishFolder(h), sheet.FileName))));
        state.Reload();
        Assert.Null(state.PublishError);
    }

    [Fact]
    public void ExportWritesTheSameDocumentOnce()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var model = new PublishModel(state, state.ActiveCollection);
        model.EnvRows[1].Share = true;
        var path = h.Dir.File("away/copy.json");
        Assert.Null(model.Export(path));
        Assert.Equal(state.ExportDocument(state.ActiveCollection, model.Intent).Serialize(), File.ReadAllBytes(path));
        Assert.False(state.IsPublished(state.ActiveCollection));   // exporting binds nothing
    }

    [Fact]
    public void PublishAgainRetriesAFailedWrite()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var folder = PublishFolder(h);
        var model = new PublishModel(state, state.ActiveCollection) { Folder = folder };
        Assert.Null(model.Publish());
        var file = Path.Combine(folder, model.FileName);
        Assert.True(File.Exists(file));

        // A file where the folder belongs fails the write on both platforms; the next store
        // change is what raises the banner.
        Directory.Delete(folder, recursive: true);
        File.WriteAllText(folder, "not a folder");
        Assert.Null(state.Upsert("d", new McpEntry(true, Connector()), null));
        Assert.Equal(state.ActiveCollection, state.PublishError?.Collection);

        File.Delete(folder);
        Directory.CreateDirectory(folder);
        // Nothing about the document or the ticks has changed since the failure, so only an
        // unconditional write puts it back — which is what pressing Publish again has to do,
        // rather than waiting for whatever the user changes next.
        var retry = new PublishModel(state, state.ActiveCollection);
        Assert.Equal(folder, retry.Folder);
        Assert.Null(retry.Publish());
        Assert.Null(state.PublishError);   // pressing Publish again is a retry
        // The change the failed write held back lands with it.
        Assert.Contains("d", CollectionDocument.Decode(File.ReadAllBytes(file)).Connectors.Keys);
    }

    [Fact]
    public void AnIllegalPathNameIsSanitizedNotDropped()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var model = new PublishModel(state, state.ActiveCollection);
        var pointer = JsonPointer.Parse("/args/0")!;
        model.PathRows[0].Marked = true;

        model.PathRows[0].Name = "my path!";
        Assert.Equal("my_path_", model.Intent.PathMarks["c"][pointer].Name);
        Assert.Contains("${CC_NEEDS:my_path_}", model.Preview, StringComparison.Ordinal);
        // A name nobody can fill must not publish the path the mark was hiding.
        Assert.False(JsonText.Contains(model.Preview, "/Users/d/x.js"));

        // A leading digit is legal in a marker name; only the space is replaced.
        model.PathRows[0].Name = "2nd path";
        Assert.Equal("2nd_path", model.Intent.PathMarks["c"][pointer].Name);

        // Nothing to make a name out of is the one case left: the row stays unmarked rather than
        // writing a marker with no name in it.
        model.PathRows[0].Name = "   ";
        Assert.False(model.Intent.PathMarks.ContainsKey("c"));
        Assert.Contains("/Users/d/x.js", model.Preview, StringComparison.Ordinal);
    }
    [Fact]
    public void TheFooterNamesTheFileAndTheOriginOnceThereIsOne()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var collection = state.ActiveCollection;
        var before = new PublishModel(state, collection);

        // Nothing published yet, so there is no origin to show and the footer is the name alone.
        Assert.Equal("", before.OriginShort);
        Assert.Equal(before.FileName, before.FooterSentence);

        Assert.Null(state.StartPublishing(collection, PublishFolder(h), PublishIntent.None));
        var origin = state.CollectionsFile.Collections[collection].Publish!.Origin;
        // A GUID, which is what the eight characters are cut from.
        Assert.Equal(36, origin.Length);

        var after = new PublishModel(state, collection);
        Assert.Equal(origin[..8], after.OriginShort);
        Assert.Equal(PublishModel.FooterLine(after.FileName, after.OriginShort), after.FooterSentence);
        Assert.Equal($"{after.FileName} · {after.OriginShort}", after.FooterSentence);
        // The document an export writes carries that same origin, so both sheets show one thing.
        Assert.Equal(origin, state.ExportDocument(collection, PublishIntent.None).Origin);
    }
    [Fact]
    public void EachSectionKnowsWhetherItHasAnythingToShow()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var model = new PublishModel(state, state.ActiveCollection);
        Assert.True(model.HasEnvRows);
        Assert.True(model.HasPathRows);

        // A remote connector carries no passthrough environment and no arguments of its own, so
        // the sheet over one has neither section.
        Assert.Null(state.CreateCollection("Remote"));
        state.Remove("c", "Remote");
        Assert.Null(state.Upsert("r", new McpEntry(AppStateHarness.Remote("https://r.example/mcp")), null, "Remote"));
        var bare = new PublishModel(state, "Remote");
        Assert.Empty(bare.EnvRows);
        Assert.Empty(bare.PathRows);
        Assert.False(bare.HasEnvRows);
        Assert.False(bare.HasPathRows);
    }

    [Fact]
    public void AnEnvRowCarriesTheValueTheTickWouldPublish()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var model = new PublishModel(state, state.ActiveCollection);

        // In full and unelided: the tick beside it is a decision about exactly these bytes.
        Assert.Equal(["A", "B"], model.EnvRows.Select(r => r.Name));
        Assert.Equal(["sk-live-secret", "us"], model.EnvRows.Select(r => r.Value));
        // Stripped until it is ticked.
        Assert.False(JsonText.Contains(model.Preview, "sk-live-secret"));
        model.EnvRows[0].Share = true;
        Assert.Contains("sk-live-secret", model.Preview, StringComparison.Ordinal);
        // The tick does not change what is there.
        Assert.Equal("sk-live-secret", model.EnvRows[0].Value);
    }

    [Fact]
    public void TheSheetOwnsItsButtonsAndItsExportTitle()
    {
        Assert.Equal("Cancel", PublishModel.CancelButton);
        Assert.Equal("Choose Folder", PublishModel.ChooseFolderButton);
        Assert.Equal("Mark as a path this machine supplies", PublishModel.MarkPathLabel);
        Assert.Equal("Export “Data team”", PublishModel.ExportTitle("Data team"));
    }

    /// <summary>
    /// The Mac has no mirror of this: its rows are structs inside @Published arrays, so editing
    /// one publishes the array. Here the rows are objects the sheet edits in place, and this is
    /// what spares the dialog a refresh of its own after every tick and keystroke.
    /// </summary>
    [Fact]
    public void EditingARowRaisesWhatIsDerivedFromItWithoutAViewCall()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var model = new PublishModel(state, state.ActiveCollection);
        var raised = new List<string>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        model.EnvRows[0].Share = true;
        Assert.Contains(nameof(PublishModel.Preview), raised);
        Assert.Contains(nameof(PublishModel.Warnings), raised);
        Assert.Contains(nameof(PublishModel.Intent), raised);

        // A keystroke in a hint counts too, and so does a path row.
        raised.Clear();
        model.EnvRows[1].Hint = "the region";
        Assert.Contains(nameof(PublishModel.Preview), raised);
        raised.Clear();
        model.PathRows[0].Marked = true;
        Assert.Contains(nameof(PublishModel.Preview), raised);
        raised.Clear();
        model.PathRows[0].Name = "server";
        Assert.Contains(nameof(PublishModel.Preview), raised);

        // Setting a row to what it already holds says nothing.
        raised.Clear();
        model.PathRows[0].Marked = true;
        Assert.Empty(raised);

        // Choosing a folder is what decides whether Publish is reachable.
        raised.Clear();
        Assert.False(model.CanPublish);
        model.Folder = PublishFolder(h);
        Assert.True(model.CanPublish);
        Assert.Contains(nameof(PublishModel.CanPublish), raised);
    }
    [Fact]
    public void AnExportCarriesOnlyTheTickedConnectors()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        Assert.Null(state.Upsert("other", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String("uvx")),
            ("env", JsonValue.Object(("OTHER_KEY", JsonValue.String("sk-other-secret")))))), null));
        var collection = state.ActiveCollection;
        var held = state.Store.Collections[collection].Mcps.Keys.Order(StringComparer.Ordinal).ToList();
        Assert.Contains("c", held);
        Assert.Contains("other", held);

        // The whole collection, as publishing always takes it.
        var whole = new PublishModel(state, collection);
        Assert.Null(whole.Connectors);
        Assert.Superset(new HashSet<string>(["c", "other"], StringComparer.Ordinal),
                        whole.EnvRows.Select(r => r.Connector).ToHashSet(StringComparer.Ordinal));
        Assert.Contains(whole.PathRows, r => r.Connector == "c");

        // One ticked name: the rows, the preview and the document all stop at it.
        var subset = new PublishModel(state, collection, ["other"]);
        Assert.Equal(["other"], subset.EnvRows.Select(r => r.Connector));
        Assert.Equal(["OTHER_KEY"], subset.EnvRows.Select(r => r.Name));
        // c's path argument is not this export's business.
        Assert.Empty(subset.PathRows);
        Assert.False(subset.HasPathRows);
        Assert.DoesNotContain("\"c\"", subset.Preview, StringComparison.Ordinal);
        Assert.Contains("other", subset.Preview, StringComparison.Ordinal);
        // The ticked connector's value is not credential-shaped.
        Assert.Empty(subset.Warnings);

        var path = h.Dir.File("one.json");
        Assert.Null(subset.Export(path));
        var document = CollectionDocument.Decode(File.ReadAllBytes(path));
        Assert.Equal(["other"], document.Connectors.Keys.Order(StringComparer.Ordinal));
        // Exporting a subset takes nothing out of the collection.
        Assert.Equal(held, state.Store.Collections[collection].Mcps.Keys.Order(StringComparer.Ordinal));
    }
}

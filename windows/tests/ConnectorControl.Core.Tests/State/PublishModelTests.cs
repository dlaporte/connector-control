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
        Assert.Equal(new PublishIntent.PathMark("srv", "your ledger clone, then dist/index.js"),
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
        Assert.DoesNotContain("sk-live-secret", model.Preview, StringComparison.Ordinal);   // the default is stripped
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
        Assert.DoesNotContain("/Users/d/x.js", model.Preview, StringComparison.Ordinal);

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
}

using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// Mirror: Tests/ConnectorControlStateTests/ReviewModelTests.swift.
/// The Review &amp; Apply sheet over a real subscription: a document on disk, a change to it, and
/// the one button that lets the change reach Claude.
/// </summary>
public class ReviewModelTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WatcherSettle = TimeSpan.FromMilliseconds(300);

    private static void WriteDocument(CollectionDocument doc, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, doc.Serialize());
    }

    /// <summary>Subscribes to the sample, then publishes a version of it with github gone and
    /// dbt's arguments changed, and waits for that to become a pending update.</summary>
    private static void Pending(AppStateHarness h, AppState state)
    {
        var path = h.Dir.File("data-team.json");
        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Subscribe(path, null));
        Thread.Sleep(WatcherSettle);
        var sample = CollectionDocumentSamples.DataTeam;
        var connectors = new Dictionary<string, CollectionDocument.Connector>(sample.Connectors, StringComparer.Ordinal);
        connectors.Remove("github");
        var dbt = connectors["dbt"];
        connectors["dbt"] = new CollectionDocument.Connector(
            new CollectionDocument.Launcher.Local("npx", ["-y", "@dbt/mcp@2"], CollectionPlatform.Mac),
            dbt.Env, dbt.Needs, dbt.Additional);
        WriteDocument(new CollectionDocument(sample.Name, sample.Author, sample.Origin, sample.Exported, connectors), path);
        TempDir.BumpModificationTime(path);
        Assert.True(h.Ui.PumpUntil(() => state.PendingUpdates.ContainsKey("Data team"), Wait));
    }

    [Fact]
    public void RowsDescribeTheDiffAndApplyLandsIt()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Pending(h, state);

        var model = new ReviewModel(state, "Data team");
        Assert.Equal("Update to Data team", model.SheetTitle);
        Assert.Equal("removes github; changes dbt", model.Summary);
        // Added, then removed, then changed.
        Assert.Equal(["github", "dbt"], model.Rows.Select(r => r.Name));
        Assert.Equal([ReviewModel.Kind.Removed, ReviewModel.Kind.Changed], model.Rows.Select(r => r.Kind));
        Assert.Equal(["github", "dbt"], model.Rows.Select(r => r.Id));

        var github = model.Rows[0];
        Assert.Equal(state.Store.Collections["Data team"].Mcps["github"].Config.EditorText(), github.Before);
        Assert.Null(github.After);
        var dbt = model.Rows[1];
        Assert.Equal(state.Store.Collections["Data team"].Mcps["dbt"].Config.EditorText(), dbt.Before);
        Assert.Equal(state.PendingDocument("Data team")!.Connectors["dbt"].Config.EditorText(), dbt.After);
        Assert.Contains("@dbt/mcp@2", dbt.After!, StringComparison.Ordinal);

        Assert.Null(model.Apply());
        Assert.Empty(state.PendingUpdates);
        Assert.Empty(model.Rows);
        Assert.Equal(string.Empty, model.Summary);
        Assert.False(state.Store.Collections["Data team"].Mcps.ContainsKey("github"));
        // A second Apply has nothing left to do and nothing to report.
        Assert.Null(model.Apply());
    }

    [Fact]
    public void AnAddedConnectorHasNoBeforeSide()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("data-team.json");
        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Subscribe(path, null));
        Thread.Sleep(WatcherSettle);
        var sample = CollectionDocumentSamples.DataTeam;
        var connectors = new Dictionary<string, CollectionDocument.Connector>(sample.Connectors, StringComparer.Ordinal)
        {
            ["jira"] = new(new CollectionDocument.Launcher.Remote("https://mcp.jira.example/", CollectionDocument.Auth.Auto, "mcp-remote", [])),
        };
        WriteDocument(new CollectionDocument(sample.Name, sample.Author, sample.Origin, sample.Exported, connectors), path);
        TempDir.BumpModificationTime(path);
        Assert.True(h.Ui.PumpUntil(() => state.PendingUpdates.ContainsKey("Data team"), Wait));

        var model = new ReviewModel(state, "Data team");
        Assert.Equal(["jira"], model.Rows.Select(r => r.Name));
        Assert.Equal(ReviewModel.Kind.Added, model.Rows[0].Kind);
        Assert.Null(model.Rows[0].Before);
        Assert.Equal(state.PendingDocument("Data team")!.Connectors["jira"].Config.EditorText(), model.Rows[0].After);
        Assert.Null(model.Apply());
        Assert.False(state.Store.Collections["Data team"].Mcps["jira"].Enabled, "an added connector arrives off");
    }

    [Fact]
    public void ApplyRefusesWhenTheSourceMovedUnderTheSheet()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Pending(h, state);
        var model = new ReviewModel(state, "Data team");
        Assert.False(model.SourceMoved);
        Assert.Equal(["github", "dbt"], model.Rows.Select(r => r.Name));

        // The author commits again while the sheet is open.
        var sample = CollectionDocumentSamples.DataTeam;
        var connectors = new Dictionary<string, CollectionDocument.Connector>(sample.Connectors, StringComparer.Ordinal);
        connectors.Remove("github");
        connectors.Remove("notion");
        var dbt = connectors["dbt"];
        connectors["dbt"] = new CollectionDocument.Connector(
            new CollectionDocument.Launcher.Local("npx", ["-y", "@dbt/mcp@3"], CollectionPlatform.Mac),
            dbt.Env, dbt.Needs, dbt.Additional);
        var path = h.Dir.File("data-team.json");
        WriteDocument(new CollectionDocument(sample.Name, sample.Author, sample.Origin, sample.Exported, connectors), path);
        TempDir.BumpModificationTime(path);
        Assert.True(h.Ui.PumpUntil(
            () => state.PendingUpdates.TryGetValue("Data team", out var d) && d.Removed.SequenceEqual(["github", "notion"]), Wait));

        // The rows on screen are not what would land.
        Assert.Equal(ReviewModel.SourceMovedMessage, model.Apply());
        Assert.True(model.SourceMoved);
        Assert.True(state.PendingUpdates.ContainsKey("Data team"));   // nothing was applied
        Assert.True(state.Store.Collections["Data team"].Mcps.ContainsKey("notion"));

        model.Refresh();
        Assert.False(model.SourceMoved);
        Assert.Equal(["github", "notion", "dbt"], model.Rows.Select(r => r.Name));
        Assert.Null(model.Apply());
        Assert.Empty(state.PendingUpdates);
        Assert.Equal(JsonValue.String("@dbt/mcp@3"),
            state.Store.Collections["Data team"].Mcps["dbt"].Config.ValueAt(JsonPointer.Parse("/args/1")!));
    }

    [Fact]
    public void ACollectionWithNothingPendingHasNoRows()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("data-team.json");
        WriteDocument(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Subscribe(path, null));

        var model = new ReviewModel(state, "Data team");
        Assert.Empty(model.Rows);
        Assert.Equal(string.Empty, model.Summary);
        Assert.Null(model.Apply());
    }
    [Fact]
    public void TheKindsGroupTheListInTheOrderTheSummaryReads()
    {
        Assert.Equal([ReviewModel.Kind.Added, ReviewModel.Kind.Removed, ReviewModel.Kind.Changed], ReviewModel.Kinds);
        Assert.Equal(["Added", "Removed", "Changed"], ReviewModel.Kinds.Select(ReviewModel.KindLabel));
        Assert.Equal(ReviewModel.AddedLabel, ReviewModel.KindLabel(ReviewModel.Kind.Added));
        Assert.Equal(ReviewModel.RemovedLabel, ReviewModel.KindLabel(ReviewModel.Kind.Removed));
        Assert.Equal(ReviewModel.ChangedLabel, ReviewModel.KindLabel(ReviewModel.Kind.Changed));
    }
    [Fact]
    public void TheSheetOwnsItsFooterButtons()
    {
        Assert.Equal("Cancel", ReviewModel.CancelButton);
        Assert.Equal("Refresh", ReviewModel.RefreshButton);
        Assert.Equal("Apply", ReviewModel.ApplyButton);
    }
}

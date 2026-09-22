using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

public class FlyoutModelTests
{
    [Fact]
    public void HeaderTextsFollowTheStore()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        using var flyout = new FlyoutModel(state, h.Settings);
        Assert.Equal("Connector Control", FlyoutModel.Title);
        Assert.Equal("No connectors configured", flyout.Subtitle);
        Assert.Equal("Default", flyout.ActiveCollection);
        Assert.True(flyout.IsEmpty);
        Assert.Equal("No connectors configured yet — add one below.", FlyoutModel.EmptyText);
        state.Upsert("z", new McpEntry(AppStateHarness.Remote("https://z.example/mcp")), null);
        Assert.Equal("1 of 1 enabled", flyout.Subtitle);
        Assert.False(flyout.IsEmpty);
    }

    [Fact]
    public void RowsAreSortedOrdinallyWithEditTooltips()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        state.Upsert("Zebra", new McpEntry(AppStateHarness.Remote("https://zebra.example/mcp")), null);
        using var flyout = new FlyoutModel(state, h.Settings);
        Assert.Equal(["Zebra", "aws-mcp", "scoutbook", "service-now"], flyout.Rows.Select(r => r.Name).ToArray());   // uppercase first: ordinal
        Assert.Equal("Edit “aws-mcp”", flyout.Rows[1].EditTooltip);
        Assert.All(flyout.Rows, r => Assert.True(r.Enabled));
    }

    [Fact]
    public void TogglingARowPersistsAndAppliesThroughAppState()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        using var flyout = new FlyoutModel(state, h.Settings);
        var row = flyout.Rows.Single(r => r.Name == "aws-mcp");
        row.Enabled = false;
        Assert.False(h.StoreOnDisk().Mcps["aws-mcp"].Enabled);
        Assert.False(h.ClaudeServers().ContainsKey("aws-mcp"));
        Assert.Equal("2 of 3 enabled", flyout.Subtitle);
        Assert.Same(row, flyout.Rows.Single(r => r.Name == "aws-mcp"));   // rows are updated in place, never replaced mid-toggle
    }

    [Fact]
    public void RowsFollowExternalStateChanges()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        using var flyout = new FlyoutModel(state, h.Settings);
        var row = flyout.Rows.Single(r => r.Name == "aws-mcp");
        state.SetEnabled("aws-mcp", false);
        Assert.False(row.Enabled);
        state.Remove("scoutbook");
        Assert.Equal(["aws-mcp", "service-now"], flyout.Rows.Select(r => r.Name).ToArray());
        state.Upsert("alpha", new McpEntry(AppStateHarness.Remote("https://alpha.example/mcp")), null);
        Assert.Equal(["alpha", "aws-mcp", "service-now"], flyout.Rows.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void TheChipMenuHasNoHousekeepingItemsAndAddIsAllowedInALocalCollection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        using var flyout = new FlyoutModel(state, h.Settings);
        Assert.Equal([new CollectionMenuItem("Default", true)], flyout.CollectionItems);
        Assert.True(flyout.CanAddConnector);

        Assert.Null(state.CreateCollection("Work"));
        // Switching only — New, Rename and Delete live in the window.
        Assert.Equal(
            [new CollectionMenuItem("Default", false), new CollectionMenuItem("Work", true)],
            flyout.CollectionItems);
        Assert.Equal("Work", flyout.ActiveCollection);
        flyout.SwitchCollection("Default");
        Assert.Equal("Default", flyout.ActiveCollection);
    }

    [Fact]
    public void ASyncedCollectionIsMarkedInTheMenuAndClosedToAdditions()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        new CollectionsFile([Sidecar("Team", new CollectionsFile.Entry(CollectionKind.Synced, "team.json"))])
            .Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        state.Reload();
        using var flyout = new FlyoutModel(state, h.Settings);
        state.PendingUpdates = new Dictionary<string, CollectionDiff>(StringComparer.Ordinal)
        {
            ["Team"] = new CollectionDiff(["jira"], [], []),
        };
        Assert.Equal(
            [new CollectionMenuItem("Default", false),
             new CollectionMenuItem("Team", true, IsSynced: true, HasPendingUpdate: true, Source: "team.json")],
            flyout.CollectionItems);
        Assert.False(flyout.CanAddConnector);
        Assert.Equal("Additions go in a local collection.", FlyoutModel.AddDisabledTooltip);
        flyout.SwitchCollection("Default");
        Assert.True(flyout.CanAddConnector);
    }

    [Fact]
    public void TheCollectionBannerCarriesItsTextAndButton()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        new CollectionsFile([
            Sidecar("Team", new CollectionsFile.Entry(CollectionKind.Synced, "team.json")),
            Sidecar("Default", new CollectionsFile.Entry(
                CollectionKind.Local, publish: new CollectionsFile.PublishRecord("default", "origin", PublishIntent.None))),
        ]).Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        // A folder that exists and can be written: a binding pointing at one that cannot would
        // raise a real publish failure on the reload below, ahead of the banner under test.
        var folder = h.Dir.File("pub");
        Directory.CreateDirectory(folder);
        new CollectionsLocalCache([], [new KeyValuePair<string, CollectionsLocalCache.PublishBinding>(
            "Default", new CollectionsLocalCache.PublishBinding(folder, null))])
            .Save(state.Service.Paths.CollectionsCachePath);
        state.Reload();
        using var flyout = new FlyoutModel(state, h.Settings);

        Assert.Equal(new CollectionBanner.Locate("Team", "team.json"), flyout.CollectionBanner);
        Assert.True(flyout.HasCollectionBanner);
        Assert.Equal("Team's file isn’t on this PC yet.", flyout.CollectionBannerText);
        Assert.Equal("Locate team.json…", flyout.CollectionBannerButton);

        state.PendingUpdates = new Dictionary<string, CollectionDiff>(StringComparer.Ordinal)
        {
            ["Team"] = new CollectionDiff(["jira"], ["confluence"], []),
        };
        Assert.Equal("Team changed at its source: adds jira; removes confluence.", flyout.CollectionBannerText);
        Assert.Equal("Review & Apply…", flyout.CollectionBannerButton);

        state.PublishError = new CollectionPublishError("Default", "the folder is read-only");
        Assert.Equal($"Couldn’t publish Default to {folder}: the folder is read-only", flyout.CollectionBannerText);
        Assert.Equal("Choose Folder…", flyout.CollectionBannerButton);

        state.PublishError = null;
        state.PendingUpdates = new Dictionary<string, CollectionDiff>(StringComparer.Ordinal);
        flyout.SwitchCollection("Default");
        // Another collection's unlocated file still gets the slot.
        Assert.Equal(new CollectionBanner.Locate("Team", "team.json"), flyout.CollectionBanner);
    }

    private static KeyValuePair<string, CollectionsFile.Entry> Sidecar(string name, CollectionsFile.Entry entry) => new(name, entry);

    [Fact]
    public void FooterPrefersRetryOverRestart()
    {
        using var h = new AppStateHarness();
        h.Claude.IsRunning = true;
        h.Claude.LaunchDate = h.Now.AddHours(-1);
        using var state = h.Create();
        using var flyout = new FlyoutModel(state, h.Settings);
        Assert.Equal(FooterKind.Hidden, flyout.Footer);
        Assert.False(flyout.ShowFooter);

        state.SetEnabled("aws-mcp", false);
        Assert.Equal(FooterKind.RestartRequired, flyout.Footer);
        Assert.Equal("Restart Required", flyout.FooterTitle);
        Assert.True(flyout.ShowFooter);

        File.WriteAllText(h.ClaudeConfigPath, "{oops");
        state.SetEnabled("scoutbook", false);   // apply fails
        Assert.Equal(FooterKind.RetryApply, flyout.Footer);
        Assert.Equal("Apply Failed — Retry", flyout.FooterTitle);
        Assert.True(flyout.HasError);

        File.WriteAllText(h.ClaudeConfigPath, Fixtures.RealisticClaudeConfig);
        flyout.FooterAction();   // retry
        Assert.Equal(FooterKind.RestartRequired, flyout.Footer);
        Assert.Equal(["service-now"], AppStateHarness.Keys(h.ClaudeServers().Keys));
    }

    [Fact]
    public async Task FooterActionRestartsWhenRestartIsRequired()
    {
        using var h = new AppStateHarness();
        h.Settings.ConfirmBeforeRestart = false;
        h.Claude.IsRunning = true;
        h.Claude.LaunchDate = h.Now.AddHours(-1);
        using var state = h.Create();
        using var flyout = new FlyoutModel(state, h.Settings);
        state.SetEnabled("aws-mcp", false);
        flyout.FooterAction();
        await Task.Yield();
        Assert.Equal(1, h.Claude.RestartCalls);
    }

    [Fact]
    public void OpenedRunsARoutineReload()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        using var flyout = new FlyoutModel(state, h.Settings);
        h.WriteClaudeServers(("scoutbook", state.Store.Mcps["scoutbook"].Config));
        flyout.Opened();
        Assert.Equal(["aws-mcp", "scoutbook", "service-now"], AppStateHarness.Keys(h.ClaudeServers().Keys));
        Assert.Equal(AppState.ClaudeConfigRegeneratedBody, h.Notifier.Sent[0].Body);
    }

    [Fact]
    public void EntryForReturnsTheLiveEntryOrNull()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        using var flyout = new FlyoutModel(state, h.Settings);
        Assert.Equal(state.Store.Mcps["scoutbook"], flyout.EntryFor("scoutbook"));
        Assert.Null(flyout.EntryFor("gone"));
    }

    [Fact]
    public void RowsCarryTheToolWarningAndFollowLaterProbeResults()
    {
        using var h = new AppStateHarness();
        h.Tools.Statuses[Tool.Npx] = ToolStatus.NotFound;
        using var state = h.Create();
        using var flyout = new FlyoutModel(state, h.Settings);
        Assert.All(flyout.Rows, r => Assert.False(r.HasToolWarning));   // nothing probed yet: no glyph
        Assert.All(flyout.Rows, r => Assert.Null(r.ToolWarning));

        state.RefreshToolsAsync([Tool.Npx]);
        Assert.True(h.Ui.PumpUntil(() => state.ToolStatuses.ContainsKey(Tool.Npx), TimeSpan.FromSeconds(5)));
        // All three seeded connectors run `npx -y mcp-remote`.
        Assert.All(flyout.Rows, r => Assert.True(r.HasToolWarning));
        Assert.All(flyout.Rows, r => Assert.Equal("Needs npx, which wasn’t found. Edit to see how to install it.", r.ToolWarning));

        // A connector whose command is a full path needs no PATH lookup, so it never warns.
        state.Upsert("pathed", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String(@"C:\Program Files\nodejs\node.exe")))), null);
        var pathed = flyout.Rows.Single(r => r.Name == "pathed");
        Assert.False(pathed.HasToolWarning);
        Assert.Null(pathed.ToolWarning);
        Assert.True(flyout.Rows.Single(r => r.Name == "aws-mcp").HasToolWarning);   // the others are unchanged

        // Installing npx: the next probe publishes Found and every glyph clears.
        h.Tools.Statuses[Tool.Npx] = new ToolStatus(@"C:\Program Files\nodejs\npx.cmd", "10.9.2");
        state.RefreshToolsAsync([Tool.Npx]);
        Assert.True(h.Ui.PumpUntil(() => flyout.Rows.All(r => !r.HasToolWarning), TimeSpan.FromSeconds(5)));
        Assert.All(flyout.Rows, r => Assert.True(r.Enabled));   // the glyph never touched the switch
    }

    [Fact]
    public void OpenedProbesOnlyTheToolsTheRowsNeedAndOnlyOnce()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        using var flyout = new FlyoutModel(state, h.Settings);
        Assert.Equal(0, h.Tools.Batches);   // building the model probes nothing

        flyout.Opened();
        Assert.True(h.Ui.PumpUntil(() => state.ToolStatuses.ContainsKey(Tool.Npx), TimeSpan.FromSeconds(5)));
        // Three npx connectors: one tool, one batch — not one probe per row.
        Assert.Equal([Tool.Npx], h.Tools.Probed.ToArray());
        Assert.Equal(1, h.Tools.Batches);

        flyout.Opened();   // everything the rows need is cached now
        Assert.Equal(1, h.Tools.Batches);
        Assert.Equal([Tool.Npx], h.Tools.Probed.ToArray());
    }

    [Fact]
    public void OpenedProbesNothingWhenNoRowNeedsATool()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        using var state = h.Create();
        using var flyout = new FlyoutModel(state, h.Settings);
        flyout.Opened();   // an empty catalog
        Assert.Equal(0, h.Tools.Batches);

        state.Upsert("pathed", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String(@"C:\tools\node.exe")))), null);
        state.Upsert("stranger", new McpEntry(JsonValue.Object(
            ("command", JsonValue.String("python")))), null);
        flyout.Opened();   // a full path and an unknown launcher both need no PATH lookup
        Assert.Equal(0, h.Tools.Batches);
        Assert.Empty(h.Tools.Probed);
        Assert.All(flyout.Rows, r => Assert.False(r.HasToolWarning));
    }

    [Fact]
    public void ANotPrivateStoreShowsInTheBannerUntilAnErrorTakesPrecedence()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        using var flyout = new FlyoutModel(state, h.Settings);
        Assert.False(flyout.HasError);
        state.StoreNotPrivate = true;
        Assert.True(flyout.HasError);
        Assert.Equal(FlyoutModel.StoreNotPrivateCaution, flyout.ErrorMessage);
        state.LastError = "apply failed";
        Assert.Equal("apply failed", flyout.ErrorMessage);
    }

    [Fact]
    public void ASettingsSaveFailureShowsInTheBannerBelowLastErrorAndStoreNotPrivate()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        using var flyout = new FlyoutModel(state, h.Settings);
        Assert.False(flyout.HasError);

        const string detail = "disk full";
        h.Settings.LastSaveError = detail;
        Assert.True(flyout.HasError);
        Assert.Equal(FlyoutModel.SettingsNotSavedCaution(detail), flyout.ErrorMessage);

        state.StoreNotPrivate = true;   // StoreNotPrivate outranks the settings-save banner
        Assert.Equal(FlyoutModel.StoreNotPrivateCaution, flyout.ErrorMessage);

        state.LastError = "apply failed";   // LastError outranks both
        Assert.Equal("apply failed", flyout.ErrorMessage);
    }
    // MARK: collection menu titles, chip marks and locks

    [Fact]
    public void TheMenuTitlesNameTheActiveCollection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        using var flyout = new FlyoutModel(state, h.Settings);
        Assert.Equal("Import…", FlyoutModel.ImportTitle);
        Assert.Equal("Manage Collections…", FlyoutModel.ManageTitle);
        Assert.Equal("Export “Default”…", flyout.ExportTitle);
        Assert.Equal(FlyoutModel.AddTooltip, flyout.AddTooltipText);

        Assert.Null(state.CreateCollection("Work"));
        Assert.Equal("Export “Work”…", flyout.ExportTitle);
    }

    [Fact]
    public void TheChipMarksAndLocksFollowTheActiveCollection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var document = h.Dir.File(Path.Combine("acme", "data-team.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(document)!);
        File.WriteAllBytes(document, CollectionDocumentSamples.DataTeam.Serialize());
        Assert.Null(state.Subscribe(document, null));
        using var flyout = new FlyoutModel(state, h.Settings);

        // A local collection: no chain, no tooltip, no locks, and additions are allowed.
        Assert.False(flyout.ActiveCollectionIsSynced);
        Assert.Null(flyout.SourceTooltip);
        Assert.False(flyout.ActiveHasPendingUpdate);
        Assert.All(flyout.Rows, r => Assert.False(r.IsLocked));

        state.SwitchCollection("Data team");
        Assert.True(flyout.ActiveCollectionIsSynced);
        Assert.Equal($"Synced from {document}", flyout.SourceTooltip);
        Assert.Equal(FlyoutModel.AddDisabledTooltip, flyout.AddTooltipText);
        Assert.Equal(["dbt", "github", "ledger", "notion"], flyout.Rows.Select(r => r.Name).ToArray());
        // Every row of a synced collection is the author's.
        Assert.All(flyout.Rows, r => Assert.True(r.IsLocked));

        // The dot is the active collection's news only.
        state.PendingUpdates = new Dictionary<string, CollectionDiff>(StringComparer.Ordinal)
        {
            ["Default"] = new CollectionDiff(["jira"], [], []),
        };
        Assert.False(flyout.ActiveHasPendingUpdate);
        state.PendingUpdates = new Dictionary<string, CollectionDiff>(StringComparer.Ordinal)
        {
            ["Data team"] = new CollectionDiff(["jira"], [], []),
        };
        Assert.True(flyout.ActiveHasPendingUpdate);
    }

    [Fact]
    public void TheSourceTooltipNamesTheSidecarFileWhileTheDocumentIsUnfound()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        new CollectionsFile([Sidecar("Team", new CollectionsFile.Entry(CollectionKind.Synced, "team.json"))])
            .Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        state.Reload();
        using var flyout = new FlyoutModel(state, h.Settings);
        Assert.Equal("Synced from team.json", flyout.SourceTooltip);
    }

    // MARK: banner actions

    [Fact]
    public void TheLocateBannerBindsTheCollectionItNames()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        new CollectionsFile([Sidecar("Team", new CollectionsFile.Entry(CollectionKind.Synced, "team.json"))])
            .Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        state.Reload();
        using var flyout = new FlyoutModel(state, h.Settings);
        Assert.Equal(new CollectionBanner.Locate("Team", "team.json"), flyout.CollectionBanner);

        // A file that is not there is not bound, and the message is the one AppState gives.
        Assert.NotNull(flyout.LocateSource(h.Dir.File("nope.json")));
        Assert.Null(state.SourceBinding("Team"));

        var document = h.Dir.File("team.json");
        File.WriteAllBytes(document, CollectionDocumentSamples.DataTeam.Serialize());
        Assert.Null(flyout.LocateSource(document));
        Assert.Equal(document, state.SourceBinding("Team")?.Path);

        // The file is found, so the banner has moved on and a second locate has nothing to act on.
        Assert.NotEqual(new CollectionBanner.Locate("Team", "team.json"), flyout.CollectionBanner);
        var elsewhere = h.Dir.File("moved.json");
        File.WriteAllBytes(elsewhere, CollectionDocumentSamples.DataTeam.Serialize());
        Assert.Null(flyout.LocateSource(elsewhere));
        Assert.Equal(document, state.SourceBinding("Team")?.Path);   // no banner, no change
    }

    [Fact]
    public void TheFailedPublishBannerRepointsTheCollectionItNames()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        new CollectionsFile([Sidecar("Default", new CollectionsFile.Entry(
            CollectionKind.Local, publish: new CollectionsFile.PublishRecord("default", "origin", PublishIntent.None)))])
            .Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        var first = h.Dir.File("first");
        Directory.CreateDirectory(first);
        new CollectionsLocalCache([], [new KeyValuePair<string, CollectionsLocalCache.PublishBinding>(
            "Default", new CollectionsLocalCache.PublishBinding(first, null))])
            .Save(state.Service.Paths.CollectionsCachePath);
        state.Reload();
        using var flyout = new FlyoutModel(state, h.Settings);

        state.PublishError = new CollectionPublishError("Default", "the folder is read-only");
        Assert.Equal(FlyoutModel.ChooseFolderButton, flyout.CollectionBannerButton);

        var second = h.Dir.File("second");
        Directory.CreateDirectory(second);
        Assert.Null(flyout.ChoosePublishFolder(second));
        Assert.Equal(second, state.CollectionsCache.Published["Default"].Folder);
        // The document lands in the folder just chosen.
        Assert.True(File.Exists(Path.Combine(second, "default.json")));
        Assert.Null(state.PublishError);

        // With the failure gone there is no banner to act on, so the folder stays put.
        var third = h.Dir.File("third");
        Directory.CreateDirectory(third);
        Assert.Null(flyout.ChoosePublishFolder(third));
        Assert.Equal(second, state.CollectionsCache.Published["Default"].Folder);
    }

    [Fact]
    public void TheCollectionsWindowRequestsRoundTripThroughAppState()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        new CollectionsFile([Sidecar("Team", new CollectionsFile.Entry(CollectionKind.Synced, "team.json"))])
            .Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        state.Reload();
        using var flyout = new FlyoutModel(state, h.Settings);
        Assert.Null(state.TakeCollectionsWindowRequest());

        flyout.RequestImport();
        Assert.Equal(new CollectionsWindowRequest.ImportFile(), state.CollectionsWindowRequest);
        Assert.Equal(new CollectionsWindowRequest.ImportFile(), state.TakeCollectionsWindowRequest());
        Assert.Null(state.CollectionsWindowRequest);   // the window takes the request once
        Assert.Null(state.TakeCollectionsWindowRequest());

        flyout.RequestExport();
        Assert.Equal(new CollectionsWindowRequest.ExportActive(), state.TakeCollectionsWindowRequest());

        // The review request comes from the banner, and the locate banner is not one.
        Assert.False(flyout.CollectionBannerAction());
        Assert.Null(state.CollectionsWindowRequest);

        state.PendingUpdates = new Dictionary<string, CollectionDiff>(StringComparer.Ordinal)
        {
            ["Team"] = new CollectionDiff(["jira"], [], []),
        };
        Assert.True(flyout.CollectionBannerAction());
        Assert.Equal(new CollectionsWindowRequest.Review("Team"), state.TakeCollectionsWindowRequest());
    }
    [Fact]
    public void OnlyTheFailedPublishBannerOffersStopPublishing()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        state.SwitchCollection("Default");
        new CollectionsFile([
            Sidecar("Team", new CollectionsFile.Entry(CollectionKind.Synced, "team.json")),
            Sidecar("Default", new CollectionsFile.Entry(
                CollectionKind.Local, publish: new CollectionsFile.PublishRecord("default", "origin", PublishIntent.None))),
        ]).Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        var folder = h.Dir.File("pub");
        Directory.CreateDirectory(folder);
        new CollectionsLocalCache([], [new KeyValuePair<string, CollectionsLocalCache.PublishBinding>(
            "Default", new CollectionsLocalCache.PublishBinding(folder, null))])
            .Save(state.Service.Paths.CollectionsCachePath);
        state.Reload();
        using var flyout = new FlyoutModel(state, h.Settings);

        // The locate banner has one button, so there is no second one to show or to press.
        Assert.Equal(new CollectionBanner.Locate("Team", "team.json"), flyout.CollectionBanner);
        Assert.Null(flyout.CollectionBannerSecondaryButton);
        flyout.CollectionBannerSecondaryAction();
        // Nothing was stopped.
        Assert.NotNull(state.CollectionsFile.Collections.GetValueOrDefault("Default")?.Publish);

        state.PublishError = new CollectionPublishError("Default", "the folder is read-only");
        Assert.Equal(CollectionsModel.StopPublishingAction, flyout.CollectionBannerSecondaryButton);
        flyout.CollectionBannerSecondaryAction();
        // The record is gone.
        Assert.Null(state.CollectionsFile.Collections.GetValueOrDefault("Default")?.Publish);
        // And so is this machine's binding.
        Assert.False(state.CollectionsCache.Published.ContainsKey("Default"));
        // With the record gone there is nothing left to have failed.
        Assert.Null(state.PublishError);
        // The document in the folder stays: a folder this machine cannot reach is not one to delete from.
        Assert.True(System.IO.File.Exists(Path.Combine(folder, "default.json")));
    }
    [Fact]
    public void AMenuRowSpellsOutWhatItsSingleImageCannotShow()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        new CollectionsFile([Sidecar("Team", new CollectionsFile.Entry(CollectionKind.Synced, "team.json"))])
            .Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        state.Reload();
        using var flyout = new FlyoutModel(state, h.Settings);

        // No news: the row is the name, and the chain is the one image the Mac can draw.
        var quiet = flyout.CollectionItems.Single(i => i.Name == "Team");
        Assert.Equal("Team", FlyoutModel.MenuTitle(quiet));

        state.PendingUpdates = new Dictionary<string, CollectionDiff>(StringComparer.Ordinal)
        {
            ["Team"] = new CollectionDiff(["jira"], [], []),
        };
        var pending = flyout.CollectionItems.Single(i => i.Name == "Team");
        Assert.Equal("Team · update available", FlyoutModel.MenuTitle(pending));
        // The mark is the dot's spoken form, so a row reads the same whether seen or heard.
        Assert.Equal(" · " + FlyoutModel.PendingSpokenLabel, FlyoutModel.PendingMenuMark);
        Assert.Equal("update available", FlyoutModel.PendingSpokenLabel);
        // One owner of the words: the window's status for the same condition.
        Assert.Equal(CollectionsModel.UpdateAvailableStatus, FlyoutModel.PendingSpokenLabel);

        // A quiet row is just its name.
        var local = flyout.CollectionItems.Single(i => i.Name == "Default");
        Assert.Equal("Default", FlyoutModel.MenuTitle(local));

        // The mark follows the news alone, not the chain: the title is pure over the flag, so a
        // row carrying news is marked whatever else it is.
        var unchained = new CollectionMenuItem("Default", false, IsSynced: false, HasPendingUpdate: true);
        Assert.Equal("Default · update available", FlyoutModel.MenuTitle(unchained));
    }

    [Fact]
    public void ARowsLockSaysWhatTheWindowsLockSays()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        using var flyout = new FlyoutModel(state, h.Settings);
        var row = flyout.Rows[0];
        Assert.Equal(CollectionsModel.LockedGlyphTooltip, row.LockTooltip);
        Assert.Equal("Read-only: synced from the collection's author", row.LockTooltip);
        // One glyph, two names.
        Assert.Equal(FlyoutModel.ToolWarningGlyph, FlyoutModel.CautionGlyph);
    }
    [Fact]
    public void EverySyncedMenuRowNamesItsOwnSource()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        Assert.Null(state.CreateCollection("Ops"));
        state.SwitchCollection("Default");
        new CollectionsFile([
            Sidecar("Team", new CollectionsFile.Entry(CollectionKind.Synced, "team.json")),
            Sidecar("Ops", new CollectionsFile.Entry(CollectionKind.Synced, "ops.json")),
        ]).Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        new CollectionsLocalCache(
            [new KeyValuePair<string, CollectionsLocalCache.SyncedBinding>(
                "Team", new CollectionsLocalCache.SyncedBinding("/Acme/mcp/team.json", null))],
            []).Save(state.Service.Paths.CollectionsCachePath);
        state.Reload();
        using var flyout = new FlyoutModel(state, h.Settings);

        CollectionMenuItem Item(string name) => flyout.CollectionItems.Single(i => i.Name == name);
        // Neither synced row is the active one, and each still names its own document: the bound
        // path where there is one, the sidecar's file name where the file is still to be found.
        Assert.Equal("/Acme/mcp/team.json", Item("Team").Source);
        Assert.Equal("Synced from /Acme/mcp/team.json", FlyoutModel.MenuTooltip(Item("Team")));
        Assert.Equal("ops.json", Item("Ops").Source);
        Assert.Equal("Synced from ops.json", FlyoutModel.MenuTooltip(Item("Ops")));
        Assert.Null(Item("Default").Source);
        Assert.Null(FlyoutModel.MenuTooltip(Item("Default")));

        // One rule for every surface: the chip, the menu and the window's sidebar all agree.
        Assert.Equal(state.SourceLocation("Team"), Item("Team").Source);
        state.SwitchCollection("Team");
        Assert.Equal(FlyoutModel.MenuTooltip(Item("Team")), flyout.SourceTooltip);
        using var window = new CollectionsModel(state, h.Dialogs);
        var sidebar = window.Items.Single(i => i.Name == "Team");
        Assert.Equal(FlyoutModel.MenuTooltip(Item("Team")), CollectionsModel.SyncedGlyphTooltip(sidebar));
    }
    [Fact]
    public void AnEmptySidecarNameAsksForNothingAnywhere()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.CreateCollection("Team"));
        // Only a hand-edited or malformed sidecar says this; nothing here writes an empty name.
        new CollectionsFile([Sidecar("Team", new CollectionsFile.Entry(CollectionKind.Synced, ""))])
            .Save(Path.Combine(h.StoreDir, CollectionsFile.FileName));
        state.Reload();
        using var flyout = new FlyoutModel(state, h.Settings);

        // The banner, the chip and the menu agree there is nothing to name: no "Locate " over a
        // blank while the tooltips stay silent.
        Assert.Null(flyout.CollectionBanner);
        Assert.Null(state.SourceLocation("Team"));
        Assert.Null(flyout.SourceTooltip);
        Assert.Null(FlyoutModel.MenuTooltip(flyout.CollectionItems.Single(i => i.Name == "Team")));
        // Nothing to find, which is what located already means.
        Assert.True(state.IsLocated("Team"));
    }
}

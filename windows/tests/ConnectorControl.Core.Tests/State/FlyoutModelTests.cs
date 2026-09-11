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
        Assert.Equal("Default ▾", flyout.ProfileChipText);
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
    public void ProfileMenuItemsAndTitles()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        using var flyout = new FlyoutModel(state, h.Settings);
        Assert.Equal([new ProfileMenuItem("Default", true)], flyout.ProfileItems);
        Assert.Equal("New Profile…", FlyoutModel.NewProfileTitle);
        Assert.Equal("Rename “Default”…", flyout.RenameProfileTitle);
        Assert.Equal("Delete “Default”…", flyout.DeleteProfileTitle);
        Assert.False(flyout.CanDeleteProfile);

        h.Dialogs.NextPromptAnswer = "Work";
        flyout.NewProfile();
        Assert.Equal([new ProfileMenuItem("Default", false), new ProfileMenuItem("Work", true)], flyout.ProfileItems);
        Assert.Equal("Work ▾", flyout.ProfileChipText);
        Assert.True(flyout.CanDeleteProfile);
        flyout.SwitchProfile("Default");
        Assert.Equal("Default ▾", flyout.ProfileChipText);
    }

    [Fact]
    public void FooterPrefersRetryOverRestart()
    {
        using var h = new AppStateHarness();
        h.Claude.IsRunning = true;
        h.Claude.LaunchTime = h.Now.AddHours(-1);
        using var state = h.Create();
        using var flyout = new FlyoutModel(state, h.Settings);
        Assert.Equal(FooterKind.None, flyout.Footer);
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
        h.Claude.LaunchTime = h.Now.AddHours(-1);
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
}

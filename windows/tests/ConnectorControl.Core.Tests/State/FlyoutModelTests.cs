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
        using var flyout = new FlyoutModel(state);
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
        using var flyout = new FlyoutModel(state);
        Assert.Equal(["Zebra", "aws-mcp", "scoutbook", "service-now"], flyout.Rows.Select(r => r.Name).ToArray());   // uppercase first: ordinal
        Assert.Equal("Edit “aws-mcp”", flyout.Rows[1].EditTooltip);
        Assert.All(flyout.Rows, r => Assert.True(r.Enabled));
    }

    [Fact]
    public void TogglingARowPersistsAndAppliesThroughAppState()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        using var flyout = new FlyoutModel(state);
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
        using var flyout = new FlyoutModel(state);
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
        using var flyout = new FlyoutModel(state);
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
        using var flyout = new FlyoutModel(state);
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
        using var flyout = new FlyoutModel(state);
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
        using var flyout = new FlyoutModel(state);
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
        using var flyout = new FlyoutModel(state);
        Assert.Equal(state.Store.Mcps["scoutbook"], flyout.EntryFor("scoutbook"));
        Assert.Null(flyout.EntryFor("gone"));
    }
}

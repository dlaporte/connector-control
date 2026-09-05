using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

public class RestoreModelTests
{
    [Fact]
    public void ListsBackupsNewestFirstWithTheOriginalLast()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        state.SetEnabled("aws-mcp", false);           // backup 1 (three servers) + original snapshot
        h.Now = h.Now.AddSeconds(2);
        state.SetEnabled("scoutbook", false);         // backup 2 (two servers)
        var model = new RestoreModel(state, h.Dialogs);
        model.Load();
        Assert.Equal(3, model.Backups.Count);
        Assert.StartsWith("claude_desktop_config.", model.BackupNames[0], StringComparison.Ordinal);
        Assert.True(string.CompareOrdinal(model.BackupNames[0], model.BackupNames[1]) > 0);   // newest first
        Assert.Equal("claude_desktop_config.original.json", model.BackupNames[2]);
        Assert.Null(model.Selection);
        Assert.False(model.CanRestore);
    }

    [Fact]
    public void RestoreConfirmsWithTheFileNameAndRestoresThroughAppState()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        state.SetEnabled("aws-mcp", false);
        var model = new RestoreModel(state, h.Dialogs);
        model.Load();
        var closed = 0;
        model.CloseRequested += () => closed++;
        model.Selection = model.Backups[0];
        Assert.True(model.CanRestore);

        h.Dialogs.NextConfirm = false;
        Assert.False(model.Restore());
        Assert.Equal(new FakeDialogs.ConfirmCall($"Replace Claude's config with {model.BackupNames[0]}?", null, "Restore", "Cancel", true), h.Dialogs.Confirms[0]);
        Assert.Equal(0, closed);

        h.Dialogs.NextConfirm = true;
        Assert.True(model.Restore());
        Assert.Equal(1, closed);
        Assert.True(state.Store.Mcps["aws-mcp"].Enabled);
        Assert.Null(model.RestoreError);
    }

    [Fact]
    public void RestoreFailureShowsInlineAndInLastError()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var bad = Path.Combine(h.BackupsDir, "claude_desktop_config.2026-09-04T00-00-00-000Z.json");
        Directory.CreateDirectory(h.BackupsDir);
        File.WriteAllText(bad, "{not json");
        var model = new RestoreModel(state, h.Dialogs);
        model.Load();
        model.Selection = bad;
        var closed = 0;
        model.CloseRequested += () => closed++;
        Assert.False(model.Restore());
        Assert.Equal("backup claude_desktop_config.2026-09-04T00-00-00-000Z.json is not a valid config file", model.RestoreError);
        Assert.Equal(model.RestoreError, state.LastError);
        Assert.True(model.HasRestoreError);
        Assert.Equal(0, closed);
    }

    [Fact]
    public void CancelClosesWithoutRestoring()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var model = new RestoreModel(state, h.Dialogs);
        var closed = 0;
        model.CloseRequested += () => closed++;
        model.Cancel();
        Assert.Equal(1, closed);
        Assert.Empty(h.Dialogs.Confirms);
    }

    [Fact]
    public void StringsMatchTheMacApp()
    {
        Assert.Equal("Restore Claude config from a backup", RestoreModel.Headline);
        Assert.Equal("The current file is backed up first, then replaced by the selected backup.", RestoreModel.Caption);
        Assert.Equal("Restore…", RestoreModel.RestoreTitle);
        Assert.Equal("Restore", RestoreModel.RestoreButton);
    }
}

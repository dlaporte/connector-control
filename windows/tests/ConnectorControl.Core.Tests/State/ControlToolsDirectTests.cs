using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>Mirror: Tests/ConnectorControlStateTests/ControlToolsDirectTests.swift</summary>
public class ControlToolsDirectTests
{
    private static JsonValue Args(params (string Key, JsonValue Value)[] arguments) => JsonValue.Object(arguments);

    private static JsonValue Text(string value) => JsonValue.String(value);

    private static McpEntry Command(string command) => new(JsonValue.Object(("command", Text(command))));

    [Fact]
    public void TurningAConnectorOffWritesClaudesConfigAndAsksForARestart()
    {
        using var rig = new ControlRig();
        rig.H.ClaudeRunningSince(hours: 1);
        rig.State.RefreshRestartState();
        var result = rig.Call("set_connector_enabled", Args(("name", Text("aws-mcp")), ("enabled", JsonValue.Bool(false))));
        Assert.NotNull(result);
        Assert.Equal(ControlTools.TurnedOffText("aws-mcp", "Default") + " " + ControlTools.RestartToApplyText, result.Text);
        Assert.Equal(JsonValue.Bool(true), result.Structured.ValueAt(new JsonPointer(["restartRequired"])));
        Assert.False(rig.H.ClaudeServers().ContainsKey("aws-mcp"));
        Assert.False(rig.State.Store.Mcps["aws-mcp"].Enabled);
        Assert.Empty(rig.Confirmer.Requests);
        Assert.Empty(rig.H.Notifier.Sent);
    }

    [Fact]
    public void TurningOnNamesNoRestartWhileClaudeIsNotRunning()
    {
        using var rig = new ControlRig();
        rig.State.SetEnabled("aws-mcp", false);
        Assert.Equal(ControlTools.TurnedOnText("aws-mcp", "Default"),
            rig.Call("set_connector_enabled", Args(("name", Text("aws-mcp")), ("enabled", JsonValue.Bool(true))))?.Text);
    }

    [Fact]
    public void OnAndOffActOnTheActiveCollectionOnly()
    {
        using var rig = new ControlRig();
        Assert.Null(rig.State.AddEmptyCollection("Work"));
        Assert.Null(rig.State.Upsert("only-in-work", Command("x"), null, "Work"));
        Assert.Equal(ControlResult.Refusal(ControlTools.NoConnectorError("only-in-work", "Default")),
            rig.Call("set_connector_enabled", Args(("name", Text("only-in-work")), ("enabled", JsonValue.Bool(false)))));
    }

    [Fact]
    public void ASubscribedCollectionsSwitchesStillWork()
    {
        using var rig = new ControlRig();
        rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam);
        rig.State.SwitchCollection("Data team");
        Assert.False(rig.Call("set_connector_enabled", Args(("name", Text("github")), ("enabled", JsonValue.Bool(true))))?.IsError);
        Assert.True(rig.State.Store.Collections["Data team"].Mcps["github"].Enabled);
    }

    [Fact]
    public void AFailedWriteSaysTheChangeWasSavedAndWhyClaudeDidNotGetIt()
    {
        using var rig = new ControlRig();
        using var block = new WriteBlock(rig.H.ClaudeConfigPath);
        Assert.True(block.IsEffective);
        var result = rig.Call("set_connector_enabled", Args(("name", Text("aws-mcp")), ("enabled", JsonValue.Bool(false))));
        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Equal(ControlTools.TurnedOffText("aws-mcp", "Default") + "\n"
            + ControlTools.SavedNotAppliedText(rig.State.LastError!), result.Text);
        Assert.Equal(JsonValue.Bool(false), result.Structured.ValueAt(new JsonPointer(["applied"])));
        Assert.True(rig.State.ApplyRetryNeeded);
    }

    [Fact]
    public void SwitchingCollections()
    {
        using var rig = new ControlRig();
        Assert.Equal(ControlResult.Refusal(MasterStore.NoCollectionError("Nope")),
            rig.Call("switch_collection", Args(("collection", Text("Nope")))));
        Assert.Null(rig.State.AddEmptyCollection("Work"));
        Assert.Equal(ControlTools.SwitchedText("Work"), rig.Call("switch_collection", Args(("collection", Text("Work"))))?.Text);
        Assert.Equal("Work", rig.State.ActiveCollection);
        Assert.Equal([ClaudeIntegration.EntryName], rig.H.ClaudeServers().Keys.ToArray());
    }

    [Fact]
    public void CreatingRenamingAndDuplicatingCollections()
    {
        using var rig = new ControlRig();
        Assert.Equal(ControlTools.CreatedText("Work"), rig.Call("create_collection", Args(("name", Text(" Work "))))?.Text);
        Assert.True(rig.Call("create_collection", Args(("name", Text("Work"))))?.IsError);
        Assert.Equal(ControlTools.RenamedCollectionText("Work", "Team"),
            rig.Call("rename_collection", Args(("collection", Text("Work")), ("newName", Text("Team"))))?.Text);
        Assert.Equal(ControlTools.DuplicatedText("Default", "Copy"),
            rig.Call("duplicate_collection", Args(("collection", Text("Default")), ("newName", Text("Copy"))))?.Text);
        Assert.Equal(rig.State.Store.Collections["Default"].Mcps.Keys.Order().ToArray(),
            rig.State.Store.Collections["Copy"].Mcps.Keys.Order().ToArray());
        Assert.Equal("Default", rig.State.ActiveCollection);
        Assert.True(rig.Call("rename_collection", Args(("collection", Text("Nope")), ("newName", Text("X"))))?.IsError);
    }

    [Fact]
    public void ASubscribedCollectionIsNotDuplicatedHere()
    {
        using var rig = new ControlRig();
        rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam);
        Assert.Equal(ControlResult.Refusal(ControlTools.DuplicateSubscribedError("Data team")),
            rig.Call("duplicate_collection", Args(("collection", Text("Data team")), ("newName", Text("Mine")))));
        Assert.False(rig.State.Store.Collections.ContainsKey("Mine"));
    }

    [Fact]
    public void RefreshingASubscription()
    {
        using var rig = new ControlRig();
        Assert.Equal(ControlResult.Refusal(ControlTools.NotSubscribedError("Default")),
            rig.Call("refresh_subscription", Args(("collection", Text("Default")))));
        var path = rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam);
        Assert.Equal(ControlTools.UpToDateText("Data team"),
            rig.Call("refresh_subscription", Args(("collection", Text("Data team"))))?.Text);
        var sample = CollectionDocumentSamples.DataTeam;
        var connectors = sample.Connectors.Where(pair => pair.Key != "github").ToList();
        AppStateHarness.WriteDocumentAt(new CollectionDocument(sample.Name, sample.Author, sample.Origin, sample.Exported, connectors), path);
        Assert.Equal(AppState.CollectionUpdateBanner("Data team", "deletes github"),
            rig.Call("refresh_subscription", Args(("collection", Text("Data team"))))?.Text);
        File.WriteAllText(path, "{half");
        Assert.True(rig.Call("refresh_subscription", Args(("collection", Text("Data team"))))?.IsError);
    }

    [Fact]
    public void SettingsAreSetThroughTheSettingsModel()
    {
        using var rig = new ControlRig();
        Assert.Equal(ControlTools.SettingChangedText("confirm-quit", "off"),
            rig.Call("set_setting", Args(("key", Text("confirm-quit")), ("value", Text("off"))))?.Text);
        Assert.False(rig.H.Settings.ConfirmBeforeQuit);
        Assert.False(rig.Call("set_setting", Args(("key", Text("launch-at-login")), ("value", Text("ON"))))?.IsError);
        Assert.True(rig.Autostart.Enabled);
        Assert.False(rig.Call("set_setting", Args(("key", Text("automatic-updates")), ("value", Text("true"))))?.IsError);
        Assert.True(rig.H.Settings.AutoUpdate);
        Assert.Equal(ControlTools.SettingChangedText("backups-to-keep", "30"),
            rig.Call("set_setting", Args(("key", Text("backups-to-keep")), ("value", Text("30"))))?.Text);
        Assert.Equal(30, rig.H.Settings.BackupKeepCount);
        // The service is rebuilt, as the Settings stepper does.
        Assert.Equal(30, rig.State.Service.Backups.KeepCount);
        Assert.Equal(ControlResult.Refusal(ControlTools.KeepCountError),
            rig.Call("set_setting", Args(("key", Text("backups-to-keep")), ("value", Text("500")))));
        Assert.Equal(ControlResult.Refusal(ControlTools.SwitchValueError("confirm-quit")),
            rig.Call("set_setting", Args(("key", Text("confirm-quit")), ("value", Text("maybe")))));
    }

    [Fact]
    public void ALoginItemThatCannotBeChangedIsRefusedWithTheAppsNote()
    {
        using var rig = new ControlRig();
        rig.Autostart.FailWith = "denied";
        Assert.Equal(ControlResult.Refusal(SettingsModel.StartupEntryFailureNote("denied")),
            rig.Call("set_setting", Args(("key", Text("launch-at-login")), ("value", Text("on")))));
    }

    [Fact]
    public void OnlyTheUserChangesWhatClaudeIsAllowedToDo()
    {
        using var rig = new ControlRig();
        foreach (var key in new[] { ControlSettingKey.ClaudeIntegration, ControlSettingKey.ConfirmClaudeChanges })
        {
            Assert.Equal(ControlResult.Refusal(ControlTools.SettingNotForClaudeError(key)),
                rig.Call("set_setting", Args(("key", Text(key)), ("value", Text("off")))));
        }
        Assert.True(rig.H.Settings.ClaudeIntegrationEnabled);
        Assert.True(rig.H.Settings.ConfirmClaudeChanges);
        var cli = new ControlCaller.Cli();
        Assert.False(rig.Call("set_setting", Args(("key", Text("confirm-claude-changes")), ("value", Text("off"))), cli)?.IsError);
        Assert.False(rig.H.Settings.ConfirmClaudeChanges);
        Assert.Equal(ControlTools.IntegrationSetText("off"),
            rig.Call("set_setting", Args(("key", Text("claude-integration")), ("value", Text("off"))), cli)?.Text);
        Assert.False(rig.H.ClaudeServers().ContainsKey(ClaudeIntegration.EntryName));
    }

    [Fact]
    public void TurningTheIntegrationOnFromTheCommandLineSaysToRestartClaude()
    {
        using var rig = new ControlRig(integration: false);
        rig.H.ClaudeRunningSince(hours: 1);
        Assert.Equal(ControlTools.IntegrationSetText("on") + " " + ControlTools.RestartToUseText,
            rig.Call("set_setting", Args(("key", Text("claude-integration")), ("value", Text("on"))), new ControlCaller.Cli())?.Text);
    }

    [Fact]
    public void TurningTheIntegrationOnFromTheCommandLineIsRefusedDuringAClash()
    {
        using var rig = new ControlRig(integration: false);
        Assert.Null(rig.State.Upsert("mine", Command("x"), null));
        rig.H.EditStoreOnDisk(store =>
        {
            var held = store.Collections[store.ActiveCollection];
            held.Mcps[ClaudeIntegration.EntryName] = held.Mcps["mine"];
        });
        rig.State.Reload(ReloadTrigger.QuietStoreAdoption);
        Assert.Equal(ControlResult.Refusal(ClaudeIntegration.ClashNote),
            rig.Call("set_setting", Args(("key", Text("claude-integration")), ("value", Text("on"))), new ControlCaller.Cli()));
    }
}

using ConnectorControl.Core.Services;
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
        // What Claude runs changed, so the user hears of it.
        Assert.Equal([(Notifications.Title, ControlTools.TurnedOffNotification("Claude", "aws-mcp") + " " + ControlTools.RestartToApplyText,
                (string?)Notifications.RestartCategory)],
            rig.H.Notifier.Sent);
    }

    /// <summary>Claude's own switches change what Claude runs without anyone being asked, so the user hears
    /// of each one that does, whatever the confirmation setting: on, off, and switching the
    /// collection, the asker and the names as the gated tools' notifications show them.</summary>
    [Fact]
    public void ClaudesSwitchesThatChangeWhatItRunsAreAnnounced()
    {
        foreach (var confirm in new[] { true, false })
        {
            using var rig = new ControlRig(confirm: confirm);
            const string work = "Work\n\u202Ez";
            Assert.Null(rig.State.AddEmptyCollection(work));
            Assert.False(rig.Call("set_connector_enabled", Args(("name", Text("aws-mcp")), ("enabled", JsonValue.Bool(false))))?.IsError);
            Assert.False(rig.Call("set_connector_enabled", Args(("name", Text("aws-mcp")), ("enabled", JsonValue.Bool(true))),
                new ControlCaller.Relay("Cursor"))?.IsError);
            Assert.False(rig.Call("switch_collection", Args(("collection", Text(work))))?.IsError);
            Assert.Equal(
                [
                    ControlTools.TurnedOffNotification("Claude", "aws-mcp"),
                    ControlTools.TurnedOnNotification(ControlCaller.CallingItselfText("Cursor"), "aws-mcp"),
                    ControlTools.SwitchedNotification("Claude", """Work\n\u{202E}z"""),
                ],
                rig.H.Notifier.Sent.Select(sent => sent.Body));
            Assert.Empty(rig.Confirmer.Requests);
        }
    }

    /// <summary>A switch that leaves what Claude runs as it was is not announced: one already in place, and
    /// the command line's, which are the user's own.</summary>
    [Fact]
    public void ASwitchThatChangesNothingClaudeRunsOrComesFromTheCommandLineIsNotAnnounced()
    {
        using var rig = new ControlRig();
        Assert.Null(rig.State.AddEmptyCollection("Work"));
        Assert.False(rig.Call("set_connector_enabled", Args(("name", Text("aws-mcp")), ("enabled", JsonValue.Bool(true))))?.IsError);
        Assert.False(rig.Call("switch_collection", Args(("collection", Text("Default"))))?.IsError);
        // Nothing Claude runs changed.
        Assert.Empty(rig.H.Notifier.Sent);
        Assert.False(rig.Call("set_connector_enabled", Args(("name", Text("aws-mcp")), ("enabled", JsonValue.Bool(false))),
            new ControlCaller.Cli())?.IsError);
        Assert.False(rig.Call("switch_collection", Args(("collection", Text("Work"))), new ControlCaller.Cli())?.IsError);
        Assert.Equal("Work", rig.State.ActiveCollection);
        // The command line's changes are the user's.
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
        // What Claude runs did not change.
        Assert.Empty(rig.H.Notifier.Sent);
    }

    /// <summary>The one setting that writes Claude's config: turned on from the command line while that write
    /// fails, it says the change was saved and why Claude did not get it, as every other change does.</summary>
    [Fact]
    public void TurningTheIntegrationOnWhileClaudesConfigCannotBeWrittenSaysItWasSaved()
    {
        using var rig = new ControlRig(integration: false);
        using var block = new WriteBlock(rig.H.ClaudeConfigPath);
        Assert.True(block.IsEffective);
        var result = rig.Call("set_setting", Args(("key", Text("claude-integration")), ("value", Text("on"))), new ControlCaller.Cli());
        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Equal(ControlTools.IntegrationSetText("on") + "\n"
            + ControlTools.SavedNotAppliedText(rig.State.LastError!), result.Text);
        Assert.Equal(JsonValue.Bool(false), result.Structured.ValueAt(new JsonPointer(["applied"])));
        Assert.True(rig.State.Settings.ClaudeIntegrationEnabled);
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
        Assert.Equal(ControlTools.PendingUpdateText("Data team", ControlTools.DeletesClause("“github”")),
            rig.Call("refresh_subscription", Args(("collection", Text("Data team"))))?.Text);
        File.WriteAllText(path, "{half");
        var unreadable = rig.Call("refresh_subscription", Args(("collection", Text("Data team"))));
        Assert.NotNull(unreadable);
        Assert.True(unreadable.IsError);
        // The app's own sentence.
        Assert.Equal(rig.State.SourceErrors["Data team"], unreadable.Text);
    }

    /// <summary>An open Settings window shows a setting the command line changed at once, as its Claude
    /// group does: its own model hears the change and reads the new values.</summary>
    [Fact]
    public void AnOpenSettingsWindowShowsASettingTheCommandLineChanged()
    {
        using var rig = new ControlRig();
        using var window = new SettingsModel(rig.State, rig.H.Settings, rig.Autostart, rig.Install, rig.Updater, rig.Updates);
        var heard = new List<System.ComponentModel.PropertyChangedEventArgs>();
        window.PropertyChanged += (_, e) => heard.Add(e);
        Assert.False(window.LaunchAtStartup);

        Assert.False(rig.Call("set_setting", Args(("key", Text("launch-at-login")), ("value", Text("on"))), new ControlCaller.Cli())?.IsError);
        Assert.True(window.LaunchAtStartup, "the one value the model keeps is read again");
        Assert.Contains(heard, e => ObservableObject.Affects(e, nameof(SettingsModel.LaunchAtStartup)));

        heard.Clear();
        Assert.False(rig.Call("set_setting", Args(("key", Text("automatic-updates")), ("value", Text("on"))), new ControlCaller.Cli())?.IsError);
        Assert.Contains(heard, e => ObservableObject.Affects(e, nameof(SettingsModel.AutoUpdate)));
        Assert.True(window.AutoUpdate);

        heard.Clear();
        Assert.False(rig.Call("set_setting", Args(("key", Text("backups-to-keep")), ("value", Text("30"))), new ControlCaller.Cli())?.IsError);
        Assert.Contains(heard, e => ObservableObject.Affects(e, nameof(SettingsModel.BackupKeepCount)));
        Assert.Equal(30, window.BackupKeepCount);
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
        Assert.False(rig.Call("set_setting", Args(("key", Text("automatic-updates")), ("value", Text("true"))), new ControlCaller.Cli())?.IsError);
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
        bool notifyBefore = rig.H.Settings.NotifyExternalChanges, updatesBefore = rig.H.Settings.AutoUpdate;
        foreach (var key in new[] { ControlSettingKey.ClaudeIntegration, ControlSettingKey.ConfirmClaudeChanges })
        {
            Assert.Equal(ControlResult.Refusal(ControlTools.SettingNotForClaudeError(key)),
                rig.Call("set_setting", Args(("key", Text(key)), ("value", Text("off")))));
        }
        // Claude may not silence the notice of synced-in changes or stop updates, either way.
        foreach (var key in new[] { ControlSettingKey.NotifyExternalChanges, ControlSettingKey.AutomaticUpdates })
        {
            foreach (var value in new[] { "on", "off" })
            {
                Assert.Equal(ControlResult.Refusal(ControlTools.SettingNotForClaudeError(key)),
                    rig.Call("set_setting", Args(("key", Text(key)), ("value", Text(value)))));
            }
        }
        Assert.True(rig.H.Settings.ClaudeIntegrationEnabled);
        Assert.True(rig.H.Settings.ConfirmClaudeChanges);
        Assert.Equal(notifyBefore, rig.H.Settings.NotifyExternalChanges);
        Assert.Equal(updatesBefore, rig.H.Settings.AutoUpdate);
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

    private static string SettingsSnapshot(ControlRig rig)
    {
        var s = rig.H.Settings;
        return $"{s.BackupKeepCount} {s.NotifyExternalChanges} {s.ConfirmBeforeRestart} {s.ConfirmBeforeQuit} "
            + $"{s.ClaudeIntegrationEnabled} {s.ConfirmClaudeChanges} {rig.Autostart.Enabled} {rig.SettingsModel.AutoUpdate}";
    }

    [Fact]
    public void TheKeepCountIsDigitsOnlyWithAsciiSpaceAndTabTrimmed()
    {
        using var rig = new ControlRig();
        foreach (var bad in new[] { "+30", "-5", "30\u00A0", "\u00A030", "3 0", "" })
        {
            Assert.Equal(ControlResult.Refusal(ControlTools.KeepCountError),
                rig.Call("set_setting", Args(("key", Text("backups-to-keep")), ("value", Text(bad)))));
        }
        Assert.Equal(20, rig.H.Settings.BackupKeepCount);
        Assert.Equal(ControlTools.SettingChangedText("backups-to-keep", "30"),
            rig.Call("set_setting", Args(("key", Text("backups-to-keep")), ("value", Text(" \t30\t "))))?.Text);
        Assert.Equal(30, rig.H.Settings.BackupKeepCount);
    }

    [Fact]
    public void TheRelayCannotReachAKeyOutsideItsAllowlist()
    {
        using var rig = new ControlRig();
        var before = SettingsSnapshot(rig);
        foreach (var key in new[] { "Claude-Integration", " claude-integration", "confirm-claude-changes ", "master-list" })
        {
            Assert.True(rig.Call("set_setting", Args(("key", Text(key)), ("value", Text("off"))))?.IsError, key);
        }
        Assert.Equal(before, SettingsSnapshot(rig));
        Assert.Empty(rig.Confirmer.Requests);
    }

    [Fact]
    public void TheCommandLineIsRefusedAnUnknownKeyWithTheNotOneOfSentence()
    {
        using var rig = new ControlRig();
        Assert.Equal(ControlResult.Refusal(ControlTools.NotOneOfError("key", string.Join(", ", ControlSettingKey.All))),
            rig.Call("set_setting", Args(("key", Text("master-list")), ("value", Text("off"))), new ControlCaller.Cli()));
    }

    [Fact]
    public void AnUnknownKeyIsRefusedWhenPreparedBeforeItsValueIsChecked()
    {
        using var rig = new ControlRig();
        foreach (ControlCaller caller in new ControlCaller[] { new ControlCaller.Cli(), new ControlCaller.Relay("claude-ai") })
        {
            var step = rig.Tools.Prepare("set_setting",
                new ControlArguments(new Dictionary<string, JsonValue> { ["key"] = Text("foo"), ["value"] = Text("maybe") }), caller);
            var refused = Assert.IsType<ControlStep.Refused>(step);
            Assert.Equal(ControlTools.NotOneOfError("key", string.Join(", ", ControlSettingKey.All)), refused.Reason);
        }
    }

    [Fact]
    public void AWriteThatChangesNothingClaudeRunsNamesNoRestart()
    {
        using var rig = new ControlRig();
        rig.H.ClaudeRunningSince(hours: 1);
        rig.State.RefreshRestartState();
        var restart = new JsonPointer(["restartRequired"]);
        var quit = rig.Call("set_setting", Args(("key", Text("confirm-quit")), ("value", Text("off"))));
        Assert.NotNull(quit);
        Assert.Equal(ControlTools.SettingChangedText("confirm-quit", "off"), quit.Text);
        Assert.Equal(JsonValue.Bool(false), quit.Structured.ValueAt(restart));
        var created = rig.Call("create_collection", Args(("name", Text("Work"))));
        Assert.NotNull(created);
        Assert.Equal(ControlTools.CreatedText("Work"), created.Text);
        Assert.Equal(JsonValue.Bool(false), created.Structured.ValueAt(restart));
        var already = rig.Call("set_connector_enabled", Args(("name", Text("aws-mcp")), ("enabled", JsonValue.Bool(true))));
        Assert.NotNull(already);
        Assert.Equal(ControlTools.TurnedOnText("aws-mcp", "Default"), already.Text);
        Assert.Equal(JsonValue.Bool(false), already.Structured.ValueAt(restart));
    }

    [Fact]
    public void AWriteThatChangesWhatClaudeRunsNamesTheRestart()
    {
        using var rig = new ControlRig();
        rig.H.ClaudeRunningSince(hours: 1);
        rig.State.RefreshRestartState();
        var restart = new JsonPointer(["restartRequired"]);
        var applied = new JsonPointer(["applied"]);
        Assert.Null(rig.State.AddEmptyCollection("Work"));
        var switched = rig.Call("switch_collection", Args(("collection", Text("Work"))));
        Assert.NotNull(switched);
        Assert.Equal(ControlTools.SwitchedText("Work") + " " + ControlTools.RestartToApplyText, switched.Text);
        Assert.Equal(JsonValue.Bool(true), switched.Structured.ValueAt(restart));
        Assert.Equal(JsonValue.Bool(true), switched.Structured.ValueAt(applied));
        var renamed = rig.Call("rename_collection", Args(("collection", Text("Work")), ("newName", Text("Team"))));
        Assert.NotNull(renamed);
        Assert.Equal(JsonValue.Bool(true), renamed.Structured.ValueAt(applied));
        // The active collection's name is not in Claude's config.
        Assert.Equal(JsonValue.Bool(false), renamed.Structured.ValueAt(restart));
        var copy = rig.Call("duplicate_collection", Args(("collection", Text("Team")), ("newName", Text("Copy"))));
        Assert.NotNull(copy);
        Assert.Equal(ControlTools.DuplicatedText("Team", "Copy"), copy.Text);
        Assert.Equal(JsonValue.Bool(false), copy.Structured.ValueAt(restart));
        Assert.Equal(JsonValue.Bool(true), copy.Structured.ValueAt(applied));
    }

    /// <summary>A write that moved what Claude's config holds only in how a character is composed has still
    /// moved it, as on the Mac, so it names the restart while Claude runs.</summary>
    [Fact]
    public void AMoveOnlyInHowACharacterIsComposedStillNamesTheRestart()
    {
        using var rig = new ControlRig();
        var composed = JsonValue.Object(("command", Text("node")), ("args", JsonValue.Array([Text("caf\u00E9.js")])));
        var decomposed = JsonValue.Object(("command", Text("node")), ("args", JsonValue.Array([Text("cafe\u0301.js")])));
        Assert.Null(rig.State.Upsert("scoutbook", new McpEntry(decomposed), "scoutbook"));
        rig.State.ApplyInteractively();
        rig.H.ClaudeRunningSince(hours: 1);
        rig.State.RefreshRestartState();
        var mark = new Dictionary<string, JsonValue>(rig.State.AppliedServers) { ["scoutbook"] = composed };
        var outcome = rig.Tools.Done(["Did it."], [], "Default", mark, ControlTools.RestartToApplyText);
        Assert.Equal("Did it. " + ControlTools.RestartToApplyText, outcome.Result.Text);
        Assert.Equal(JsonValue.Bool(true), outcome.Result.Structured.ValueAt(new JsonPointer(["restartRequired"])));
    }

    [Fact]
    public void AStaleRetryFlagDoesNotColourAnUnrelatedSetting()
    {
        using var rig = new ControlRig();
        using (var block = new WriteBlock(rig.H.ClaudeConfigPath))
        {
            Assert.True(block.IsEffective);
            rig.Call("set_connector_enabled", Args(("name", Text("aws-mcp")), ("enabled", JsonValue.Bool(false))));
        }
        Assert.True(rig.State.ApplyRetryNeeded);
        var result = rig.Call("set_setting", Args(("key", Text("confirm-quit")), ("value", Text("off"))));
        Assert.NotNull(result);
        Assert.Equal(ControlTools.SettingChangedText("confirm-quit", "off"), result.Text);
        Assert.Equal(JsonValue.Bool(true), result.Structured.ValueAt(new JsonPointer(["applied"])));
        Assert.Equal(ControlTools.SettingChangedText("backups-to-keep", "30"),
            rig.Call("set_setting", Args(("key", Text("backups-to-keep")), ("value", Text("30"))))?.Text);
    }

    // MARK: names

    /// <summary>Nothing in <paramref name="text"/> that a line would show as an escape: nothing that breaks a
    /// line, or reorders or hides text.</summary>
    private static void AssertPlain(string? text) => Assert.DoesNotContain(text ?? "", c => ControlText.Escape(c) is not null);

    /// <summary>A direct change's answer is also what announces it when it lands late, and it names
    /// connectors and collections as others chose them: Claude creates and renames collections
    /// without asking. Each name shows as an escape what would break a line or reorder or hide text,
    /// and so does the app's own refusal that echoes one.</summary>
    [Fact]
    public void ADirectChangeShowsNamesAsEscapes()
    {
        using var rig = new ControlRig();
        const string work = "Work\n\u202Ez", shownWork = """Work\n\u{202E}z""";
        const string team = "Team\n\u202Ey", shownTeam = """Team\n\u{202E}y""";
        const string copy = "Copy\u2066”", shownCopy = """Copy\u{2066}\u{201D}""";
        const string name = "a\nb\u202Ec", shownName = """a\nb\u{202E}c""";
        const string data = "Data\n\u202Eteam", shownData = """Data\n\u{202E}team""";
        var results = new List<ControlResult?>
        {
            rig.Call("create_collection", Args(("name", Text(work)))),
            rig.Call("rename_collection", Args(("collection", Text(work)), ("newName", Text(team)))),
            rig.Call("duplicate_collection", Args(("collection", Text(team)), ("newName", Text(copy)))),
            rig.Call("switch_collection", Args(("collection", Text(team)))),
        };
        Assert.Null(rig.State.Upsert(name, Command("x"), null));
        results.Add(rig.Call("set_connector_enabled", Args(("name", Text(name)), ("enabled", JsonValue.Bool(false)))));
        results.Add(rig.Call("set_connector_enabled", Args(("name", Text("\u202E")), ("enabled", JsonValue.Bool(false)))));
        results.Add(rig.Call("switch_collection", Args(("collection", Text("\u202E")))));
        results.Add(rig.Call("refresh_subscription", Args(("collection", Text(team)))));
        results.Add(rig.Call("rename_collection", Args(("collection", Text(copy)), ("newName", Text(team)))));
        rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam, collection: data);
        results.Add(rig.Call("refresh_subscription", Args(("collection", Text(data)))));
        Assert.Equal([
            ControlTools.CreatedText(shownWork),
            ControlTools.RenamedCollectionText(shownWork, shownTeam),
            ControlTools.DuplicatedText(shownTeam, shownCopy),
            ControlTools.SwitchedText(shownTeam),
            ControlTools.TurnedOffText(shownName, shownTeam),
            ControlTools.NoConnectorError("""\u{202E}""", shownTeam),
            MasterStore.NoCollectionError("""\u{202E}"""),
            ControlTools.NotSubscribedError(shownTeam),
            """A collection named “Team\n\u{202E}y” already exists.""",
            ControlTools.UpToDateText(shownData),
        ], results.Select(result => result?.Text));
        foreach (var result in results)
        {
            AssertPlain(result?.Text);
        }
        // Each change was made as asked.
        Assert.False(rig.State.Store.Collections[team].Mcps[name].Enabled);
    }

    /// <summary>A direct change Claude asked for that lands after its early answer is announced in the
    /// same words, names escaped.</summary>
    [Fact]
    public void ALateDirectChangeIsAnnouncedWithNamesAsEscapes()
    {
        using var rig = new ControlRig();
        const string name = "a\nb\u202Ec";
        Assert.Null(rig.State.Upsert(name, Command("x"), null));
        rig.Start("add_connector", Args(("name", Text("held")), ("config", JsonValue.Object(("command", Text("node"))))));
        var queued = rig.Start("set_connector_enabled", Args(("name", Text(name)), ("enabled", JsonValue.Bool(false))));
        for (var tick = 1; tick <= 20 && rig.H.Delays.Pending.Count > 0; tick++)
        {
            rig.H.Delays.RunNext();
        }
        Assert.Equal(ControlTools.QueuedResult, queued.Result);
        rig.Confirmer.Answer(false);
        Assert.Equal([ControlTools.TurnedOffText("""a\nb\u{202E}c""", "Default")], rig.H.Notifier.Sent.Select(sent => sent.Body));
    }

    /// <summary>A switch Claude asked for that lands after its early answer and moves what Claude runs is
    /// announced once, in its notification's words.</summary>
    [Fact]
    public void ALateSwitchThatChangesWhatClaudeRunsIsAnnouncedOnce()
    {
        using var rig = new ControlRig();
        rig.Start("add_connector", Args(("name", Text("held")), ("config", JsonValue.Object(("command", Text("node"))))));
        var queued = rig.Start("set_connector_enabled", Args(("name", Text("aws-mcp")), ("enabled", JsonValue.Bool(false))));
        for (var tick = 1; tick <= 20 && rig.H.Delays.Pending.Count > 0; tick++)
        {
            rig.H.Delays.RunNext();
        }
        Assert.Equal(ControlTools.QueuedResult, queued.Result);
        rig.Confirmer.Answer(false);
        Assert.False(rig.State.Store.Mcps["aws-mcp"].Enabled);
        Assert.Equal([ControlTools.TurnedOffNotification("Claude", "aws-mcp")], rig.H.Notifier.Sent.Select(sent => sent.Body));
        Assert.Equal(1, queued.Completions);
    }
}

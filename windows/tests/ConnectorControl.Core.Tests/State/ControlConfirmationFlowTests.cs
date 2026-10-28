using ConnectorControl.Core.Services;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// The confirmation flow on the real gated tools: what the user's answer does, what the caller
/// hears, and what is announced when nobody was asked or the caller had already been answered.
///
/// Mirror: Tests/ConnectorControlStateTests/ControlConfirmationFlowTests.swift
/// </summary>
public class ControlConfirmationFlowTests
{
    private static readonly JsonValue Config = JsonValue.Object(
        ("command", JsonValue.String("node")), ("args", JsonValue.Array([JsonValue.String("server.js")])));

    private static JsonValue Add(string name) => JsonValue.Object(("name", JsonValue.String(name)), ("config", Config));

    [Fact]
    public void DecliningChangesNothing()
    {
        using var rig = new ControlRig();
        rig.Confirmer.Scripted = false;
        var before = File.ReadAllBytes(rig.H.ClaudeConfigPath);
        Assert.Equal(ControlResult.Refusal(ControlTools.DeclinedError), rig.Call("add_connector", Add("x")));
        Assert.False(rig.State.Store.Mcps.ContainsKey("x"));
        Assert.Equal(before, File.ReadAllBytes(rig.H.ClaudeConfigPath));
        Assert.Empty(rig.H.Notifier.Sent);
    }

    [Fact]
    public void WithConfirmationsOffAChangeAppliesAndIsAnnounced()
    {
        using var rig = new ControlRig(confirm: false);
        rig.H.ClaudeRunningSince(hours: 1);
        Assert.False(rig.Call("add_connector", Add("x"))?.IsError);
        Assert.Empty(rig.Confirmer.Requests);
        Assert.Equal([ControlTools.AddedNotification("Claude", "x", "Default") + " " + ControlTools.RestartToUseText],
            rig.H.Notifier.Sent.Select(sent => sent.Body));
        Assert.Equal(Notifications.RestartCategory, rig.H.Notifier.Sent[^1].Category);
    }

    [Fact]
    public void TheAnnouncementIgnoresNotifyOnExternalChanges()
    {
        using var rig = new ControlRig(confirm: false);
        rig.H.Settings.NotifyExternalChanges = false;
        Assert.False(rig.Call("add_connector", Add("x"))?.IsError);
        Assert.Equal([ControlTools.AddedNotification("Claude", "x", "Default")], rig.H.Notifier.Sent.Select(sent => sent.Body));
    }

    /// <summary>A rename and nothing else is announced as a rename; a rename that also changes the config is
    /// announced as a change, under the new name.</summary>
    [Fact]
    public void ARenameIsAnnouncedAsARename()
    {
        using var rig = new ControlRig(confirm: false);
        rig.Call("update_connector", JsonValue.Object(("name", JsonValue.String("scoutbook")), ("newName", JsonValue.String("scouts"))));
        rig.Call("update_connector", JsonValue.Object(("name", JsonValue.String("scouts")), ("newName", JsonValue.String("scoutbook")),
            ("config", Config)));
        Assert.Equal([ControlTools.RenamedNotification("Claude", "scoutbook", "scouts"),
                      ControlTools.ChangedNotification("Claude", "scoutbook", "Default")], rig.H.Notifier.Sent.Select(sent => sent.Body));
    }

    /// <summary>An announcement shows names as the question would: what would break a line or reorder or
    /// hide text shows as an escape.</summary>
    [Fact]
    public void AnAnnouncementShowsNamesAsEscapes()
    {
        using var rig = new ControlRig(confirm: false);
        const string collection = "Work\n\u202Ez", shownCollection = """Work\n\u{202E}z""";
        var caller = new ControlCaller.Relay("Cursor\n\u202Ed");
        const string shownAsker = """Cursor\n\u{202E}d""";
        Assert.Null(rig.State.AddEmptyCollection(collection));
        var target = ("collection", JsonValue.String(collection));
        rig.Call("add_connector", JsonValue.Object(target, ("name", JsonValue.String("a\nb")), ("config", Config)), caller);
        rig.Call("update_connector", JsonValue.Object(target, ("name", JsonValue.String("a\nb")), ("newName", JsonValue.String("c\u202E"))),
            caller);
        rig.Call("delete_connectors", JsonValue.Object(target, ("names", JsonValue.Array([JsonValue.String("c\u202E")]))), caller);
        Assert.Equal([ControlTools.AddedNotification(shownAsker, """a\nb""", shownCollection),
                      ControlTools.RenamedNotification(shownAsker, """a\nb""", """c\u{202E}"""),
                      ControlTools.DeletedNotification(shownAsker, """“c\u{202E}”""", shownCollection)],
            rig.H.Notifier.Sent.Select(sent => sent.Body));
    }

    [Fact]
    public void TheCommandLineIsNeverAskedAndNeverAnnounced()
    {
        using var rig = new ControlRig();
        Assert.False(rig.Call("add_connector", Add("x"), new ControlCaller.Cli())?.IsError);
        Assert.Empty(rig.Confirmer.Requests);
        Assert.Empty(rig.H.Notifier.Sent);
    }

    [Fact]
    public void AnotherClientIsNamedInTheQuestion()
    {
        using var rig = new ControlRig();
        rig.Confirmer.Scripted = true;
        rig.Call("add_connector", Add("x"), new ControlCaller.Relay("Cursor"));
        Assert.Equal(ControlTools.AddTitle("Cursor", "x"), rig.Confirmer.Requests[^1].Title);
    }

    [Fact]
    public void AWaitingCallReportsProgressThenSaysItIsWaitingAndALateAllowStillApplies()
    {
        using var rig = new ControlRig();
        var answer = rig.Start("add_connector", Add("x"));
        Assert.Null(answer.Result);
        Assert.Equal(1, rig.Confirmer.WaitingCount);
        for (var tick = 1; tick < 10; tick++)
        {
            rig.H.Delays.RunNext();
        }
        Assert.Equal([5.0, 10, 15, 20, 25, 30, 35, 40, 45], answer.Progress);
        Assert.Null(answer.Result);
        rig.H.Delays.RunNext();
        Assert.Equal(50, answer.Progress[^1]);
        Assert.Equal(new ControlResult(JsonValue.Object(("status", JsonValue.String("waiting"))), ControlTools.WaitingText, false),
            answer.Result);
        // The ticking stops once the caller has its answer.
        Assert.Empty(rig.H.Delays.Pending);
        rig.Confirmer.Answer(true);
        // The dialog stayed, and the answer still counts.
        Assert.True(rig.State.Store.Mcps.ContainsKey("x"));
        // Announced once, since the caller had already been answered.
        Assert.Equal([ControlTools.AddedNotification("Claude", "x", "Default")], rig.H.Notifier.Sent.Select(sent => sent.Body));
        // The caller hears once.
        Assert.Equal(ControlTools.WaitingText, answer.Result?.Text);
        Assert.Equal(1, answer.Completions);
    }

    [Fact]
    public void ALateDeclineAppliesNothingAndSaysNothing()
    {
        using var rig = new ControlRig();
        var answer = rig.Start("add_connector", Add("x"));
        for (var tick = 1; tick <= 10; tick++)
        {
            rig.H.Delays.RunNext();
        }
        Assert.Equal(ControlTools.WaitingText, answer.Result?.Text);
        rig.Confirmer.Answer(false);
        Assert.False(rig.State.Store.Mcps.ContainsKey("x"));
        Assert.Empty(rig.H.Notifier.Sent);
        Assert.Equal(1, answer.Completions);
    }

    [Fact]
    public void AnAnswerBeforeTheWaitRunsOutIsTheAnswer()
    {
        using var rig = new ControlRig();
        var answer = rig.Start("add_connector", Add("x"));
        rig.H.Delays.RunNext();
        rig.Confirmer.Answer(true);
        Assert.Equal(ControlTools.AddedText("x", "Default", ControlTools.OnWord), answer.Result?.Text);
        Assert.Equal([5.0], answer.Progress);
        rig.H.Delays.RunNext();
        Assert.Empty(rig.H.Delays.Pending);
        // A tick after the answer reports nothing.
        Assert.Equal([5.0], answer.Progress);
        // The caller heard the outcome itself.
        Assert.Empty(rig.H.Notifier.Sent);
    }

    /// <summary>The app's dialog is modal: <c>Confirm</c> returns only once the user has answered, while
    /// its run loop runs what the host delays. The wait starts before the question is put, so the caller
    /// still hears progress and, at <see cref="ControlTools.ConfirmationWait"/>, that the answer is
    /// awaited; the Allow that follows applies and is announced once.</summary>
    [Fact]
    public void AModalDialogStillLetsTheCallerHearItIsWaiting()
    {
        using var rig = new ControlRig();
        var delays = rig.H.Delays;
        rig.Confirmer.Scripted = true;
        rig.Confirmer.WhileOpen = () =>
        {
            while (delays.Pending.Count > 0)
            {
                delays.RunNext();
            }
        };
        var answer = rig.Start("add_connector", Add("x"));
        Assert.Equal([5.0, 10, 15, 20, 25, 30, 35, 40, 45, 50], answer.Progress);
        Assert.Equal(ControlTools.WaitingResult, answer.Result);
        Assert.Equal(1, answer.Completions);
        // The Allow, given after the early answer, still counts.
        Assert.True(rig.State.Store.Mcps.ContainsKey("x"));
        // Announced once, since the caller had already been answered.
        Assert.Equal([ControlTools.AddedNotification("Claude", "x", "Default")], rig.H.Notifier.Sent.Select(sent => sent.Body));
        Assert.Empty(rig.H.Delays.Pending);
    }

    [Fact]
    public void WritesRunOneAtATimeAndReadsDoNotWait()
    {
        using var rig = new ControlRig();
        var first = rig.Start("add_connector", Add("a"));
        var second = rig.Start("add_connector", Add("b"));
        // The second question waits behind the first.
        Assert.Single(rig.Confirmer.Requests);
        // A read answers at once.
        Assert.NotNull(rig.Call("get_status"));
        var direct = rig.Start("set_connector_enabled",
            JsonValue.Object(("name", JsonValue.String("aws-mcp")), ("enabled", JsonValue.Bool(false))));
        // A direct change waits its turn too.
        Assert.Null(direct.Result);
        rig.Confirmer.Answer(true);
        Assert.False(first.Result?.IsError);
        Assert.Equal(2, rig.Confirmer.Requests.Count);
        Assert.Null(second.Result);
        rig.Confirmer.Answer(true);
        Assert.False(second.Result?.IsError);
        Assert.False(direct.Result?.IsError);
        var servers = rig.H.ClaudeServers();
        Assert.True(servers.ContainsKey("a") && servers.ContainsKey("b"));
        Assert.False(servers.ContainsKey("aws-mcp"));
    }

    /// <summary>A call queued behind an open dialog has asked nobody anything, so its early answer says it
    /// is waiting its turn rather than for an answer; its turn still comes.</summary>
    [Fact]
    public void AQueuedCallHearsItIsWaitingTooAndStillRunsInTurn()
    {
        using var rig = new ControlRig();
        rig.Start("add_connector", Add("a"));
        var queued = rig.Start("add_connector", Add("b"));
        for (var tick = 1; tick <= 20; tick++)
        {
            if (rig.H.Delays.Pending.Count > 0)
            {
                rig.H.Delays.RunNext();
            }
        }
        Assert.Equal(ControlTools.QueuedResult, queued.Result);
        rig.Confirmer.Answer(true);
        rig.Confirmer.Answer(true);
        Assert.True(rig.State.Store.Mcps.ContainsKey("b"));
        Assert.Equal(1, queued.Completions);
    }

    [Fact]
    public void AChangeThatMovedWhileAskingIsNotApplied()
    {
        using var rig = new ControlRig();
        var answer = rig.Start("add_connector", Add("x"));
        Assert.Null(rig.State.AddEmptyCollection("Work"));
        rig.State.SwitchCollection("Work");
        rig.Confirmer.Answer(true);
        // The question named Default as the collection Claude runs; it no longer is.
        Assert.Equal(ControlResult.Refusal(ControlTools.ChangedWhileAskingError), answer.Result);
        Assert.False(rig.State.Store.Collections["Default"].Mcps.ContainsKey("x"));
    }

    [Fact]
    public void AConnectorThatChangedWhileAskingIsLeftAsTheUserMadeIt()
    {
        using var rig = new ControlRig();
        var answer = rig.Start("update_connector", JsonValue.Object(("name", JsonValue.String("scoutbook")), ("config", Config)));
        var theirs = AppStateHarness.Remote("https://scoutbook.example.com/v3/mcp");
        var entry = rig.State.Store.Mcps["scoutbook"];
        Assert.Null(rig.State.Upsert("scoutbook", new McpEntry(entry.Enabled, theirs), "scoutbook"));
        rig.Confirmer.Answer(true);
        // The question showed a change from a config the connector no longer has.
        Assert.Equal(ControlResult.Refusal(ControlTools.ChangedWhileAskingError), answer.Result);
        Assert.Equal(theirs, rig.State.Store.Mcps["scoutbook"].Config);
        Assert.Empty(rig.H.Notifier.Sent);
    }

    /// <summary>The question asked again is compared with the one the user answered ordinally, as on the
    /// Mac: a connector that changed only in how a character is composed while the dialog was open has
    /// changed, and nothing is done.</summary>
    [Fact]
    public void AChangeWhileAskingOnlyInHowACharacterIsComposedStillCounts()
    {
        using var rig = new ControlRig();
        var composed = JsonValue.Object(("command", JsonValue.String("node")), ("args", JsonValue.Array([JsonValue.String("caf\u00E9.js")])));
        var decomposed = JsonValue.Object(("command", JsonValue.String("node")), ("args", JsonValue.Array([JsonValue.String("cafe\u0301.js")])));
        Assert.Null(rig.State.Upsert("scoutbook", new McpEntry(composed), "scoutbook"));
        var answer = rig.Start("update_connector", JsonValue.Object(("name", JsonValue.String("scoutbook")), ("config", Config)));
        Assert.Null(rig.State.Upsert("scoutbook", new McpEntry(decomposed), "scoutbook"));
        rig.Confirmer.Answer(true);
        Assert.Equal(ControlResult.Refusal(ControlTools.ChangedWhileAskingError), answer.Result);
        // Nothing was done.
        Assert.NotEqual(Config, rig.State.Store.Mcps["scoutbook"].Config);
    }

    [Fact]
    public void ARefusalThatAroseWhileAskingWins()
    {
        using var rig = new ControlRig();
        var answer = rig.Start("add_connector", Add("x"));
        Assert.Null(rig.State.Upsert("x", new McpEntry(Config), null));
        rig.Confirmer.Answer(true);
        Assert.Equal(ControlResult.Refusal(AppState.DuplicateNameError("x")), answer.Result);
    }

    [Fact]
    public void AWriteQueuedWhenTheIntegrationGoesOffIsRefused()
    {
        using var rig = new ControlRig();
        rig.Start("add_connector", Add("a"));
        var queued = rig.Start("add_connector", Add("b"));
        rig.State.SetClaudeIntegrationEnabled(false);
        rig.Confirmer.Answer(true);
        Assert.Equal(ControlResult.Refusal(ControlTools.IntegrationOffError), queued.Result);
        Assert.Single(rig.Confirmer.Requests);
        Assert.False(rig.State.Store.Mcps.ContainsKey("b"));
    }

    [Fact]
    public void AnEditorOpenOnTheConnectorStillCatchesTheChange()
    {
        using var rig = new ControlRig();
        rig.Confirmer.Scripted = true;
        var entry = rig.State.Store.Mcps["scoutbook"];
        using var editor = new EditorModel(rig.State, EditTarget.Existing("scoutbook", entry, "Default"), rig.H.Dialogs,
            RemoteLaunchStyle.Npx);
        rig.Call("update_connector", JsonValue.Object(("name", JsonValue.String("scoutbook")), ("config", Config)));
        rig.H.Dialogs.NextConfirm = false;
        // The editor's own changed-outside check still guards it.
        Assert.False(editor.Save());
        Assert.Equal(EditorModel.ChangedOutsideMessage("scoutbook"), rig.H.Dialogs.Confirms[^1].Message);
        // Claude's change stands.
        Assert.Equal(Config, rig.State.Store.Mcps["scoutbook"].Config);
    }
}

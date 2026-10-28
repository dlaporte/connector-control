using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// The write queue, driven by writes the test controls: every write is prepared through the tools'
/// <c>PrepareOverride</c>, asks a question of its own when its tool asks, and records that it ran.
///
/// Mirror: Tests/ConnectorControlStateTests/ControlWriteQueueTests.swift
/// </summary>
public class ControlWriteQueueTests
{
    /// <summary>The writes that ran, in order, by the name or collection each was given.</summary>
    private readonly List<string> performed = [];

    private static readonly ControlCaller Claude = new ControlCaller.Relay("claude-ai");

    private static ConfirmationRequest Question(string label) => new($"Allow {label}?", label, [], null);

    /// <summary>Every write prepared as one that asks about its label, and answers "Did &lt;label&gt;." once it
    /// runs. <paramref name="beforePerform"/>, when given, runs first, and may throw.</summary>
    private void ControlWrites(ControlRig rig, Action<string>? beforePerform = null) =>
        rig.Tools.PrepareOverride = (name, arguments, _) =>
        {
            var label = arguments.String("collection") ?? arguments.String("name") ?? name;
            return new ControlStep.Ready(Question(label), () =>
            {
                beforePerform?.Invoke(label);
                performed.Add(label);
                return new ControlOutcome(new ControlResult(JsonValue.Object(), $"Did {label}.", false), null);
            });
        };

    /// <summary>A gated write: it asks while confirmations are on.</summary>
    private static ControlRig.Answer Add(ControlRig rig, string name) =>
        rig.Start("add_connector", JsonValue.Object(("name", JsonValue.String(name)), ("config", JsonValue.Object())));

    /// <summary>A direct write: it never asks.</summary>
    private static ControlRig.Answer SwitchTo(ControlRig rig, string collection, ControlCaller? caller = null) =>
        rig.Start("switch_collection", JsonValue.Object(("collection", JsonValue.String(collection))), caller);

    /// <summary>Lets every waiting call's clock run out: each reports progress until its early answer.</summary>
    private static void RunOutTheClock(ControlRig rig)
    {
        var runs = 0;
        while (rig.H.Delays.Pending.Count > 0 && runs < 100)
        {
            rig.H.Delays.RunNext();
            runs++;
        }
        // Every waiting call has had its early answer.
        Assert.Empty(rig.H.Delays.Pending);
    }

    [Fact]
    public void AnAllowAfterTheIntegrationWasTurnedOffAppliesNothing()
    {
        using var rig = new ControlRig();
        ControlWrites(rig);
        var answer = Add(rig, "a");
        Assert.Equal(1, rig.Confirmer.WaitingCount);
        Assert.Null(rig.State.SetClaudeIntegrationEnabled(false));
        rig.Confirmer.Answer(true);
        Assert.Equal(ControlResult.Refusal(ControlTools.IntegrationOffError), answer.Result);
        Assert.Empty(performed);
    }

    [Fact]
    public void AWriteWaitingItsTurnIsDecidedAgainWhenItComes()
    {
        using var rig = new ControlRig();
        ControlWrites(rig);
        var asking = Add(rig, "a");
        var queued = SwitchTo(rig, "b");
        // It waits behind the open dialog.
        Assert.Null(queued.Result);
        Assert.Null(rig.State.SetClaudeIntegrationEnabled(false));
        rig.Confirmer.Answer(false);
        Assert.Equal(ControlResult.Refusal(ControlTools.DeclinedError), asking.Result);
        Assert.Equal(ControlResult.Refusal(ControlTools.IntegrationOffError), queued.Result);
        Assert.Empty(performed);
    }

    /// <summary>Turning confirmations off loosens the gate rather than closing it: the Allow the user gave
    /// still applies.</summary>
    [Fact]
    public void AnAllowAfterConfirmationsWereTurnedOffStillApplies()
    {
        using var rig = new ControlRig();
        ControlWrites(rig);
        var answer = Add(rig, "a");
        rig.State.SetConfirmClaudeChanges(false);
        rig.Confirmer.Answer(true);
        Assert.Equal("Did a.", answer.Result?.Text);
        Assert.Equal(["a"], performed);
    }

    [Fact]
    public void AWriteWaitingItsTurnSaysSoAndOnlyItsOwnDialogSaysItAwaitsAnAnswer()
    {
        using var rig = new ControlRig();
        ControlWrites(rig);
        var asking = Add(rig, "a");
        var queued = SwitchTo(rig, "b");
        RunOutTheClock(rig);
        var ticks = Enumerable.Range(1, (int)(ControlTools.ConfirmationWait / ControlTools.ProgressInterval))
            .Select(tick => tick * ControlTools.ProgressInterval).ToList();
        Assert.Equal(ControlTools.WaitingResult, asking.Result);
        Assert.Equal(ticks, asking.Progress);
        Assert.Equal(ControlTools.QueuedResult, queued.Result);
        Assert.Equal(ticks, queued.Progress);
        Assert.NotEqual(ControlTools.WaitingText, ControlTools.QueuedText);
        Assert.Empty(performed);
    }

    [Fact]
    public void AChangeClaudeAskedForThatLandsAfterItsEarlyAnswerIsAnnounced()
    {
        using var rig = new ControlRig();
        ControlWrites(rig);
        Add(rig, "a");
        SwitchTo(rig, "b");
        RunOutTheClock(rig);
        SwitchTo(rig, "c", new ControlCaller.Cli());
        rig.H.Notifier.Sent.Clear();
        rig.Confirmer.Answer(true);
        rig.H.Drain();
        Assert.Equal(["a", "b", "c"], performed);
        // The confirmed change and the direct one Claude asked for are announced; the command
        // line's own change is the user's.
        Assert.Equal(["Did a.", "Did b."], rig.H.Notifier.Sent.Select(sent => sent.Body));
    }

    [Fact]
    public void AChangeAnsweredInTimeIsNotAnnouncedAgain()
    {
        using var rig = new ControlRig();
        ControlWrites(rig);
        var asking = Add(rig, "a");
        var queued = SwitchTo(rig, "b");
        rig.H.Notifier.Sent.Clear();
        rig.Confirmer.Answer(true);
        rig.H.Drain();
        Assert.Equal("Did a.", asking.Result?.Text);
        Assert.Equal("Did b.", queued.Result?.Text);
        // The caller heard the outcome itself.
        Assert.Empty(rig.H.Notifier.Sent);
    }

    [Fact]
    public void AToolThatBuildsNoQuestionIsRefusedRatherThanRunUnasked()
    {
        using var rig = new ControlRig();
        rig.Tools.PrepareOverride = (_, _, _) => new ControlStep.Ready(null, () =>
        {
            performed.Add("unasked");
            return new ControlOutcome(new ControlResult(JsonValue.Object(), "Did it.", false), null);
        });
        Assert.Equal(ControlResult.Refusal(ControlTools.NoQuestionError), Add(rig, "a").Result);
        Assert.Empty(performed);
        Assert.Empty(rig.Confirmer.Requests);
    }

    // C# only, as each case below is: a C# handler, dialog or caller can throw, where Swift's
    // closures cannot, and a write that threw must still end and let the next one run.

    [Fact]
    public void AWriteThatThrowsIsRefusedOnceAndTheNextStillRuns()
    {
        using var rig = new ControlRig();
        ControlWrites(rig, label =>
        {
            if (label is "a" or "b")
            {
                throw new InvalidOperationException($"{label} broke");
            }
        });
        var direct = new List<ControlResult>();
        rig.Tools.Call("switch_collection", JsonValue.Object(("collection", JsonValue.String("a"))), Claude, _ => { }, direct.Add);
        Assert.Equal([ControlResult.Refusal("a broke")], direct);
        var confirmed = Add(rig, "b");
        rig.Confirmer.Answer(true);
        Assert.Equal(ControlResult.Refusal("b broke"), confirmed.Result);
        Assert.Equal("Did c.", SwitchTo(rig, "c").Result?.Text);
        Assert.Equal(["c"], performed);
    }

    [Fact]
    public void ADialogThatThrowsRefusesTheCallOnceAndTheNextStillRuns()
    {
        using var rig = new ControlRig();
        ControlWrites(rig);
        rig.Confirmer.Throws = new InvalidOperationException("no dialog");
        var answers = new List<ControlResult>();
        rig.Tools.Call("add_connector", JsonValue.Object(("name", JsonValue.String("a")), ("config", JsonValue.Object())),
            Claude, _ => { }, answers.Add);
        Assert.Equal([ControlResult.Refusal("no dialog")], answers);
        // The wait's tick, set before the question was put, finds the call answered and says nothing more.
        Assert.Equal([TimeSpan.FromSeconds(ControlTools.ProgressInterval)], rig.H.Delays.Pending.Select(pending => pending.Delay));
        rig.H.Delays.RunNext();
        Assert.Single(answers);
        Assert.Empty(rig.H.Delays.Pending);
        rig.Confirmer.Throws = null;
        rig.Confirmer.Scripted = true;
        Assert.Equal("Did b.", Add(rig, "b").Result?.Text);
        Assert.Equal(["b"], performed);
    }

    [Fact]
    public void ANotifierThatThrowsLeavesAnAppliedChangeASuccess()
    {
        using var rig = new ControlRig(confirm: false);
        ControlWrites(rig);
        rig.H.Notifier.Throws = new InvalidOperationException("no toasts");
        var answers = new List<ControlResult>();
        rig.Tools.Call("add_connector", JsonValue.Object(("name", JsonValue.String("a")), ("config", JsonValue.Object())),
            Claude, _ => { }, answers.Add);
        // The change applied, so Claude hears it did, once, and has no reason to ask again.
        Assert.Equal([new ControlResult(JsonValue.Object(), "Did a.", false)], answers);
        Assert.Equal("Did b.", SwitchTo(rig, "b").Result?.Text);
        Assert.Equal(["a", "b"], performed);
    }

    [Fact]
    public void ACallerThatThrowsOnItsAnswerStillLetsTheNextWriteRun()
    {
        using var rig = new ControlRig();
        ControlWrites(rig);
        var answers = 0;
        rig.Tools.Call("switch_collection", JsonValue.Object(("collection", JsonValue.String("a"))), Claude, _ => { },
            _ =>
            {
                answers++;
                throw new InvalidOperationException("caller gone");
            });
        Assert.Equal(1, answers);
        Assert.Equal("Did b.", SwitchTo(rig, "b").Result?.Text);
        Assert.Equal(["a", "b"], performed);
    }

    /// <summary>Starts a write whose connection the test closes by clearing <paramref name="open"/>.</summary>
    private static ControlRig.Answer Start(ControlRig rig, string name, JsonValue arguments, ControlCaller caller, Func<bool> open)
    {
        var answer = new ControlRig.Answer();
        rig.Tools.Call(name, arguments, caller, answer.Progress.Add, result =>
        {
            answer.Result = result;
            answer.Completions++;
        }, () => !open());
        rig.H.Drain();
        return answer;
    }

    /// <summary>The command line is not bound by Claude's time limit: queued behind a dialog, it keeps hearing
    /// progress, is never answered early, and is answered once its own change has run.</summary>
    [Fact]
    public void ACommandLineWriteQueuedBehindADialogWaitsForItsTurn()
    {
        using var rig = new ControlRig();
        ControlWrites(rig);
        var asking = Add(rig, "a");
        var fromCli = SwitchTo(rig, "c", new ControlCaller.Cli());
        for (var i = 0; i < 40; i++)
        {
            rig.H.Delays.RunNext();
        }
        Assert.Equal(ControlTools.WaitingResult, asking.Result);
        // No early answer, however long it waits.
        Assert.Null(fromCli.Result);
        // It hears progress past the 50 s.
        Assert.True(fromCli.Progress.LastOrDefault() > ControlTools.ConfirmationWait);
        rig.Confirmer.Answer(false);
        Assert.Equal("Did c.", fromCli.Result?.Text);
        Assert.Equal(1, fromCli.Completions);
        Assert.Equal(["c"], performed);
    }

    /// <summary>A write whose connection closed before its turn — a command line stopped with Control-C — is
    /// dropped unrun: nobody is left to hear of it, and the queue moves on.</summary>
    [Fact]
    public void AWriteWhoseConnectionClosedBeforeItsTurnIsDroppedUnrun()
    {
        using var rig = new ControlRig();
        ControlWrites(rig);
        var open = true;
        Add(rig, "a");
        var gone = Start(rig, "switch_collection", JsonValue.Object(("collection", JsonValue.String("c"))), new ControlCaller.Cli(), () => open);
        open = false;
        rig.Confirmer.Answer(false);
        Assert.Null(gone.Result);
        Assert.Empty(performed);
        // The next write runs.
        Assert.Equal("Did d.", SwitchTo(rig, "d").Result?.Text);
    }

    /// <summary>A relay write already told it is queued still runs when its turn comes, as it was told, even
    /// if its connection has closed since.</summary>
    [Fact]
    public void ARelayWriteToldItIsQueuedRunsAfterItsConnectionCloses()
    {
        using var rig = new ControlRig();
        ControlWrites(rig);
        var open = true;
        Add(rig, "a");
        var told = Start(rig, "switch_collection", JsonValue.Object(("collection", JsonValue.String("b"))), Claude, () => open);
        RunOutTheClock(rig);
        Assert.Equal(ControlTools.QueuedResult, told.Result);
        open = false;
        rig.H.Notifier.Sent.Clear();
        rig.Confirmer.Answer(false);
        rig.H.Drain();
        Assert.Equal(["b"], performed);
        // Announced once, since it will never hear the answer.
        Assert.Equal(["Did b."], rig.H.Notifier.Sent.Select(sent => sent.Body));
    }

    /// <summary>A relay write waiting its turn whose connection closes before its early answer hears nothing
    /// more — no progress, no "queued" — and is dropped when its turn comes: Claude was never told it
    /// was waiting, and a retry after "restarted — try again" must not land it twice.</summary>
    [Fact]
    public void ARelayWriteWhoseConnectionClosesBeforeItsEarlyAnswerIsDropped()
    {
        using var rig = new ControlRig();
        ControlWrites(rig);
        var open = true;
        Add(rig, "a");
        var queued = Start(rig, "switch_collection", JsonValue.Object(("collection", JsonValue.String("b"))), Claude, () => open);
        // The two calls tick in turn: eighteen runs take each to 45 s.
        for (var i = 0; i < 18; i++)
        {
            rig.H.Delays.RunNext();
        }
        Assert.Equal(9, queued.Progress.Count);
        open = false;
        RunOutTheClock(rig);
        // No early answer once the connection has gone, and no more progress.
        Assert.Null(queued.Result);
        Assert.Equal(9, queued.Progress.Count);
        rig.Confirmer.Answer(false);
        Assert.Null(queued.Result);
        Assert.Empty(performed);
    }

    /// <summary>A change Claude asked for whose connection has gone is still the user's to allow; allowed, it
    /// lands and is announced once, since its caller will never hear the answer.</summary>
    [Fact]
    public void AnAllowedChangeWhoseConnectionClosedIsAnnouncedOnce()
    {
        using var rig = new ControlRig();
        ControlWrites(rig);
        var open = true;
        Start(rig, "add_connector", JsonValue.Object(("name", JsonValue.String("a")), ("config", JsonValue.Object())), Claude, () => open);
        open = false;
        rig.H.Notifier.Sent.Clear();
        rig.Confirmer.Answer(true);
        rig.H.Drain();
        Assert.Equal(["a"], performed);
        Assert.Equal(["Did a."], rig.H.Notifier.Sent.Select(sent => sent.Body));
    }

    /// <summary>An asking call whose connection closes hears no more progress and is never told it is waiting;
    /// the user's Allow still applies, and the change is announced once.</summary>
    [Fact]
    public void AnAskingCallWhoseConnectionClosesHearsNothingMoreButStillLandsOnAllow()
    {
        using var rig = new ControlRig();
        ControlWrites(rig);
        var open = true;
        var asking = Start(rig, "add_connector", JsonValue.Object(("name", JsonValue.String("a")), ("config", JsonValue.Object())), Claude, () => open);
        for (var i = 0; i < 4; i++)
        {
            rig.H.Delays.RunNext();
        }
        Assert.Equal(4, asking.Progress.Count);
        open = false;
        RunOutTheClock(rig);
        // Never told it is waiting, and no more progress.
        Assert.Null(asking.Result);
        Assert.Equal(4, asking.Progress.Count);
        rig.H.Notifier.Sent.Clear();
        rig.Confirmer.Answer(true);
        rig.H.Drain();
        Assert.Equal(["a"], performed);
        Assert.Equal(["Did a."], rig.H.Notifier.Sent.Select(sent => sent.Body));
        Assert.Equal(1, asking.Completions);
        // The answer goes to a connection that is closed, and is dropped there.
        Assert.Equal("Did a.", asking.Result?.Text);
    }

    /// <summary>Dropping a write moves on to the next, which may be dropped too: two abandoned writes in a row
    /// both go, and the write after them runs.</summary>
    [Fact]
    public void TwoAbandonedWritesInARowAreBothDroppedAndTheNextRuns()
    {
        using var rig = new ControlRig();
        ControlWrites(rig);
        var open = true;
        Add(rig, "a");
        var first = Start(rig, "switch_collection", JsonValue.Object(("collection", JsonValue.String("b"))), new ControlCaller.Cli(), () => open);
        var second = Start(rig, "switch_collection", JsonValue.Object(("collection", JsonValue.String("c"))), new ControlCaller.Cli(), () => open);
        var third = SwitchTo(rig, "d", new ControlCaller.Cli());
        open = false;
        rig.Confirmer.Answer(false);
        Assert.Null(first.Result);
        Assert.Null(second.Result);
        Assert.Equal("Did d.", third.Result?.Text);
        Assert.Equal(["d"], performed);
    }
}

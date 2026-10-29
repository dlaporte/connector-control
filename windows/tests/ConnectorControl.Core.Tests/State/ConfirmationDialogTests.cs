using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>Mirror: Tests/ConnectorControlStateTests/ConfirmationDialogTests.swift</summary>
public class ConfirmationDialogTests
{
    private static ConfirmationRequest Question(string title, string message = "", IReadOnlyList<string>? code = null, string? footer = null) =>
        new(title, message, code ?? [], footer);

    private static readonly ConfirmationRequest Restart = new(ControlTools.RestartItselfTitle, ControlTools.RestartMessage, [], null);

    // MARK: Text

    [Fact]
    public void NamesAreSetApartFromTheSentence()
    {
        var dialog = new ConfirmationDialog(Question(ControlTools.DeleteTitle("Claude", ControlText.QuotedList(["aws-billing", "splunk"])),
                                                     ControlTools.DeleteActiveMessage("Harvard")));
        Assert.Equal([new TextRun("Claude wants to delete “"), new TextRun("aws-billing", IsSetApart: true),
                      new TextRun("” and “"), new TextRun("splunk", IsSetApart: true), new TextRun("”")], dialog.Title);
        Assert.Equal([new TextRun("From “"), new TextRun("Harvard", IsSetApart: true),
                      new TextRun("”, the collection Claude is running. A copy remains in Backups.")], dialog.Message);
        Assert.Empty(dialog.Code);
        Assert.Null(dialog.Footer);
        // A question naming no one is one run.
        Assert.Equal([new TextRun(ControlTools.RestartItselfTitle)], new ConfirmationDialog(Restart).Title);
    }

    /// <summary>A name's own “ and ” are escaped before it reaches a question, and a quote that only looks like
    /// one stays inside the name's run, drawn in the name's face, where it cannot pass for its end.</summary>
    [Fact]
    public void AQuoteInsideANameCannotEndIt()
    {
        Assert.Equal([new TextRun("Claude wants to add “"), new TextRun("""a\u{201D} and \u{201C}b""", IsSetApart: true), new TextRun("”")],
            new ConfirmationDialog(Question(ControlTools.AddTitle("Claude", ControlText.EscapedName("a” and “b")))).Title);
        Assert.Equal([new TextRun("Claude wants to add “"), new TextRun("a＂ and ＂b", IsSetApart: true), new TextRun("”")],
            new ConfirmationDialog(Question(ControlTools.AddTitle("Claude", ControlText.EscapedName("a＂ and ＂b")))).Title);
        Assert.Equal([new TextRun("An app calling itself “"), new TextRun("Cursor", IsSetApart: true), new TextRun("” wants to add “"),
                      new TextRun("x", IsSetApart: true), new TextRun("”")],
            new ConfirmationDialog(Question(ControlTools.AddTitle(new ControlCaller.Relay("Cursor").ShownAsker, "x"))).Title);
    }

    [Fact]
    public void ALongNameIsCutAndTheRestOfTheQuestionStays()
    {
        var longName = new string('x', 100);
        Assert.Equal([new TextRun("Claude wants to add “"), new TextRun(new string('x', 63) + TextRun.CutMark, IsSetApart: true),
                      new TextRun("”")],
            new ConfirmationDialog(Question(ControlTools.AddTitle("Claude", longName))).Title);
        var exact = new string('y', ConfirmationDialog.NameLimit);
        // A name at the limit is whole.
        Assert.Equal(new TextRun(exact, IsSetApart: true), new ConfirmationDialog(Question(ControlTools.AddTitle("Claude", exact))).Title[1]);
        // Counted in scalars, as the Mac mirror counts them: 64 of these are 128 UTF-16 units.
        var wide = string.Concat(Enumerable.Repeat("👍", ConfirmationDialog.NameLimit));
        Assert.Equal(wide, new ConfirmationDialog(Question(ControlTools.AddTitle("Claude", wide))).Title[1].Text);
    }

    [Fact]
    public void AQuoteNeverClosedOrAnEmptyNameStaysWords()
    {
        Assert.Equal([new TextRun("a “b")], TextRun.Names("a “b", 64));
        Assert.Equal([new TextRun("“” x")], TextRun.Names("“” x", 64));
        Assert.Empty(TextRun.Names("", 64));
    }

    [Fact]
    public void APathOrCommandInACaptionIsSetApart()
    {
        Assert.Equal([new TextRun("Scripts can always use "), new TextRun("/x/connector-control", IsSetApart: true)],
            TextRun.Runs("Scripts can always use /x/connector-control", ["/x/connector-control"]));
        Assert.Equal([new TextRun("Run "), new TextRun("connector-control help", IsSetApart: true), new TextRun(" in Terminal.")],
            TextRun.Runs("Run connector-control help in Terminal.", ["connector-control help"]));
        Assert.Equal([new TextRun("Not installed")], TextRun.Runs("Not installed", ["", "/x"]));
    }

    // MARK: What would run

    [Fact]
    public void CodeLinesKeepTheirTextAndSayWhatAChangeRemovesAndAdds()
    {
        var lines = ControlMasking.ChangeLines(JsonValue.Object(("url", JsonValue.String("https://a.example/mcp"))),
                                               JsonValue.Object(("url", JsonValue.String("https://b.example/mcp"))), ControlMasking.Exposure.Dialog);
        var dialog = new ConfirmationDialog(Question("t", "m", lines, ControlTools.TakesEffectFooter));
        Assert.Equal(lines, dialog.Code);
        Assert.Equal([ConfirmationDialog.LineKind.Plain, ConfirmationDialog.LineKind.Removed, ConfirmationDialog.LineKind.Added], dialog.CodeKinds);
        Assert.Equal([new TextRun(ControlTools.TakesEffectFooter)], dialog.Footer);
        // An update lists each connector under its heading, its lines indented once.
        var update = ControlTools.Indented(lines).Prepend(ControlTools.ConnectorHeading("splunk", ReviewModel.ChangedLabel)).ToList();
        Assert.Equal([ConfirmationDialog.LineKind.Plain, ConfirmationDialog.LineKind.Plain, ConfirmationDialog.LineKind.Removed,
                      ConfirmationDialog.LineKind.Added], new ConfirmationDialog(Question("t", code: update)).CodeKinds);
        // A line that continues a field is padded past its label, so a variable named "−" stays plain.
        var added = ControlMasking.CodeLines(JsonValue.Object(("command", JsonValue.String("npx")),
            ("env", JsonValue.Object(("A", JsonValue.String("1")), ("\u2212", JsonValue.String("2"))))), ControlMasking.Exposure.Dialog);
        Assert.Equal([ConfirmationDialog.LineKind.Plain, ConfirmationDialog.LineKind.Plain, ConfirmationDialog.LineKind.Plain],
            new ConfirmationDialog(Question("t", code: added)).CodeKinds);
        Assert.Equal([ConfirmationDialog.LineKind.Plain, ConfirmationDialog.LineKind.Plain, ConfirmationDialog.LineKind.Plain],
            new ConfirmationDialog(Question("t", code: ControlTools.Indented(added).ToList())).CodeKinds);
    }

    /// <summary>Text is compared ordinally, as the Mac mirror compares it scalar by scalar: a name composed
    /// another way is another name.</summary>
    [Fact]
    public void RunsAndDialogsCompareTextScalarByScalar()
    {
        Assert.NotEqual(new TextRun("caf\u00E9", IsSetApart: true), new TextRun("cafe\u0301", IsSetApart: true));
        Assert.NotEqual(new TextRun("x", IsSetApart: true), new TextRun("x"));
        Assert.NotEqual(new ConfirmationDialog(Question("t", code: ["caf\u00E9"])), new ConfirmationDialog(Question("t", code: ["cafe\u0301"])));
        Assert.Equal(new ConfirmationDialog(Restart), new ConfirmationDialog(Restart));
    }

    /// <summary>The file a subscription reads is set apart in the footer, whole: which file it is is the question.</summary>
    [Fact]
    public void ThePathInTheFooterIsSetApartWhole()
    {
        const string path = "/Users/me/Library/CloudStorage/OneDrive-Example/Team/a-rather-long-folder-name/team-tools.json";
        var dialog = new ConfirmationDialog(Question("t", footer: ControlTools.FromFooter(ControlText.EscapedPath(path))));
        Assert.Equal([new TextRun("From “"), new TextRun(path, IsSetApart: true), new TextRun("”")], dialog.Footer);
    }

    /// <summary>Each set-apart piece is isolated where it is drawn, so right-to-left names side by side keep their
    /// order; words are drawn as they are.</summary>
    [Fact]
    public void ASetApartPieceIsIsolatedWhereItIsDrawn()
    {
        // Platform idiom: Windows embeds where the Mac isolates, since WPF's text engine predates isolates.
        Assert.Equal("\u202Aאבג\u202C", new TextRun("אבג", IsSetApart: true).ShownText);
        Assert.Equal("words", new TextRun("words").ShownText);
    }

    // MARK: The confirmer

    /// <summary><c>Confirm</c> returns before anything is shown; the dialog comes on a turn of its own and its
    /// answer is the call's, once.</summary>
    [Fact]
    public void TheQuestionIsPutOnATurnOfItsOwnAndAnsweredOnce()
    {
        var dialogs = new FakeDialogs();
        var turns = new List<Action>();
        var confirmer = new DialogConfirmer(dialogs, turns.Add);
        var answers = new List<bool>();
        confirmer.Confirm(Restart, answers.Add);
        // Confirm returns before the dialog is shown.
        Assert.Empty(dialogs.ChangeQuestions);
        Assert.Single(turns);
        dialogs.NextChangeAnswer = true;
        TakeFirst(turns)();
        Assert.Equal([new ConfirmationDialog(Restart)], dialogs.ChangeQuestions);
        Assert.Equal([true], answers);
        confirmer.Confirm(Restart, answers.Add);
        dialogs.NextChangeAnswer = false;
        TakeFirst(turns)();
        // Don’t Allow, or the window closed, answers false, once.
        Assert.Equal([true, false], answers);
    }

    /// <summary>The confirmer the app uses, end to end over the tools: while the dialog is up, the caller hears
    /// it is waiting at 50 s, and the Allow given after that applies and is announced once, whatever
    /// "Notify on external changes" says.</summary>
    [Fact]
    public void ALateAllowThroughTheDialogAppliesAndIsAnnouncedOnce()
    {
        using var rig = new ControlRig();
        rig.H.Settings.NotifyExternalChanges = false;
        var turns = new List<Action>();
        var tools = new ControlTools(rig.State, rig.SettingsModel, new DialogConfirmer(rig.H.Dialogs, turns.Add), rig.H.Host);
        var answer = new ControlRig.Answer();
        tools.Call("add_connector",
            JsonValue.Object(("name", JsonValue.String("x")), ("config", JsonValue.Object(("command", JsonValue.String("npx"))))),
            new ControlCaller.Relay("claude-ai"), answer.Progress.Add, result =>
            {
                answer.Result = result;
                answer.Completions++;
            });
        rig.H.Drain();
        Assert.Null(answer.Result);
        ControlResult? heardWhileAsking = null;
        var appliedWhileAsking = true;
        // The ticks run while the question is up, as the modal dialog's own loop runs them.
        rig.H.Dialogs.DuringChange = () =>
        {
            for (var tick = 0; tick < 10; tick++)
            {
                rig.H.Delays.RunNext();
            }
            heardWhileAsking = answer.Result;
            appliedWhileAsking = rig.State.Store.Mcps.ContainsKey("x");
        };
        rig.H.Dialogs.NextChangeAnswer = true;
        TakeFirst(turns)();
        rig.H.Drain();
        Assert.Equal(ControlTools.WaitingResult, heardWhileAsking);
        // Nothing is applied before the answer.
        Assert.False(appliedWhileAsking);
        // The caller hears once.
        Assert.Equal(ControlTools.WaitingResult, answer.Result);
        Assert.Equal(new TextRun("Claude wants to add “"), rig.H.Dialogs.ChangeQuestions[0].Title[0]);
        Assert.True(rig.State.Store.Mcps.ContainsKey("x"));
        Assert.Equal(1, answer.Completions);
        Assert.Equal([ControlTools.AddedNotification("Claude", "x", "Default")], rig.H.Notifier.Sent.Select(sent => sent.Body));
    }

    /// <summary>Platform idiom: a WPF dialog can throw once it may be showing, which a Swift dialog cannot. That
    /// is Don’t Allow, answered once, never a throw: the write queue waits for exactly one answer.</summary>
    [Fact]
    public void ADialogThatThrowsOnceShownAnswersDontAllowOnce()
    {
        var dialogs = new FakeDialogs { ChangeFailure = new InvalidOperationException("no window") };
        var turns = new List<Action>();
        var confirmer = new DialogConfirmer(dialogs, turns.Add);
        var answers = new List<bool>();
        confirmer.Confirm(Restart, answers.Add);
        Assert.Empty(answers);
        turns[0]();
        Assert.Equal([false], answers);
    }

    /// <summary>The first turn, taken off the list, as the Mac's removeFirst takes it.</summary>
    private static Action TakeFirst(List<Action> turns)
    {
        var turn = turns[0];
        turns.RemoveAt(0);
        return turn;
    }
}

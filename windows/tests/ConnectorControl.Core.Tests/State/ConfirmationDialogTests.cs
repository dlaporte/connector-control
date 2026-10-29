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
        var longName = new string('a', 50) + new string('z', 50);
        Assert.Equal([new TextRun("Claude wants to add “"),
                      new TextRun(new string('a', 31) + TextRun.CutMark + new string('z', 32), IsSetApart: true),
                      new TextRun("”")],
            new ConfirmationDialog(Question(ControlTools.AddTitle("Claude", longName))).Title);
        var exact = new string('y', ConfirmationDialog.NameLimit);
        // A name at the limit is whole.
        Assert.Equal(new TextRun(exact, IsSetApart: true), new ConfirmationDialog(Question(ControlTools.AddTitle("Claude", exact))).Title[1]);
        // Counted in scalars, as the Mac mirror counts them: 64 of these are 128 UTF-16 units.
        var wide = string.Concat(Enumerable.Repeat("👍", ConfirmationDialog.NameLimit));
        Assert.Equal(wide, new ConfirmationDialog(Question(ControlTools.AddTitle("Claude", wide))).Title[1].Text);
    }

    /// <summary>A long name is cut in the middle, so two names that differ only at their ends read apart, and the
    /// cut's own "…" is the only raw one: a name's own shows as its code.</summary>
    [Fact]
    public void NamesPastTheLimitStillReadApart()
    {
        var prefix = new string('p', 63);
        var one = new ConfirmationDialog(Question(ControlTools.DeleteTitle("Claude", ControlText.QuotedList([prefix + "-prod-1"]))));
        var other = new ConfirmationDialog(Question(ControlTools.DeleteTitle("Claude", ControlText.QuotedList([prefix + "-prod-2"]))));
        Assert.NotEqual(one.Title, other.Title);
        Assert.Equal(new string('p', 31) + TextRun.CutMark + new string('p', 25) + "-prod-1", one.Title[1].Text);
        Assert.Equal(ConfirmationDialog.NameLimit, ControlText.Width(one.Title[1].Text));
        // A name that ends in its own "…" cannot pass for one that was cut.
        Assert.Equal(@"name\u{2026}", ControlText.EscapedName("name\u2026"));
        var mimic = new string('m', 63) + "\u2026";
        Assert.False(new ConfirmationDialog(Question(ControlTools.AddTitle("Claude", ControlText.EscapedName(mimic)))).Title[1].Text
            .EndsWith(new string('m', 32) + TextRun.CutMark, StringComparison.Ordinal));
    }

    /// <summary>The name an app gives itself is cut as any other name is, wherever it is shown: a megabyte of it
    /// pushes neither the question nor what a notification says happened out of sight.</summary>
    [Fact]
    public void AnAppsOwnNameIsCutLikeAnyOtherName()
    {
        var name = new string('a', 1_000_000 - 3) + "end";
        var cut = new string('a', 31) + TextRun.CutMark + new string('a', 29) + "end";
        var asker = new ControlCaller.Relay(name).ShownAsker;
        Assert.Equal(ControlCaller.CallingItselfText(cut), asker);
        Assert.Equal([new TextRun("An app calling itself “"), new TextRun(cut, IsSetApart: true), new TextRun("” wants to add “"),
                      new TextRun("x", IsSetApart: true), new TextRun("”")],
            new ConfirmationDialog(Question(ControlTools.AddTitle(asker, "x"))).Title);
        Assert.Equal($"An app calling itself “{cut}” added “x” to “Harvard”.", ControlTools.AddedNotification(asker, "x", "Harvard"));
        var exact = new string('e', ConfirmationDialog.NameLimit);
        // A name at the limit is whole.
        Assert.Equal(ControlCaller.CallingItselfText(exact), new ControlCaller.Relay(exact).ShownAsker);
    }

    /// <summary>The cut never splits a character's escape: an escape is kept whole on its side of the "…", or left
    /// out whole.</summary>
    [Fact]
    public void TheCutNeverSplitsAnEscape()
    {
        // An escape across the 31st scalar, and one across the last 32.
        var name = ControlText.EscapedName(new string('a', 27) + "\u202E" + new string('b', 40) + "\u202E" + new string('c', 27));
        var shown = new ConfirmationDialog(Question(ControlTools.AddTitle("Claude", name))).Title[1].Text;
        Assert.Equal(new string('a', 27) + TextRun.CutMark + new string('c', 27), shown);
        var backslashes = ControlText.EscapedName(new string('\\', 40));
        var cut = new ConfirmationDialog(Question(ControlTools.AddTitle("Claude", backslashes))).Title[1].Text;
        Assert.Equal(string.Concat(Enumerable.Repeat(@"\\", 15)) + TextRun.CutMark + string.Concat(Enumerable.Repeat(@"\\", 16)), cut);
        Assert.True(ControlText.Width(cut) <= ConfirmationDialog.NameLimit);
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

    /// <summary>The object replacement character is drawn as nothing, so it shows as its code: a rename that only
    /// adds one, or a variable whose name only gains one, reads as the change it is.</summary>
    [Fact]
    public void AnObjectReplacementCharacterShowsAsItsCode()
    {
        Assert.Equal(@"Renamed “github” to “github\u{FFFC}”.",
            ControlTools.RenamedText(ControlText.EscapedName("github"), ControlText.EscapedName("github\uFFFC")));
        var rename = new ConfirmationDialog(Question(ControlTools.ChangeTitle("Claude", ControlText.EscapedName("github")),
            code: [ControlMasking.RemovedPrefix + ControlText.EscapedName("github"), ControlMasking.AddedPrefix + ControlText.EscapedName("github\uFFFC")]));
        Assert.Equal(ControlMasking.AddedPrefix + @"github\u{FFFC}", rename.Code[^1]);
        var lines = ControlMasking.ChangeLines(
            JsonValue.Object(("command", JsonValue.String("npx")), ("env", JsonValue.Object(("READ_ONLY", JsonValue.String("1"))))),
            JsonValue.Object(("command", JsonValue.String("npx")), ("env", JsonValue.Object(("READ_ONLY\uFFFC", JsonValue.String("1"))))),
            ControlMasking.Exposure.Dialog);
        Assert.Contains(lines, line => line.Contains(@"READ_ONLY\u{FFFC}", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains('\uFFFC'));
        Assert.Equal("a\uFFFD", ControlText.Escaped("a\uFFFD")); // the replacement character shows, so it stays
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

    /// <summary>Each code line is drawn with every character left to right in the order it is stored, so letters
    /// that run right to left cannot carry the numbers and arguments beside them into another order; what
    /// the question holds, and what Allow compares, stays the line itself.</summary>
    [Fact]
    public void ACodeLineIsDrawnInTheOrderItIsStored()
    {
        var line = "--ports \u0627 80 443 22";
        var request = Question("t", code: [line, "plain"]);
        var dialog = new ConfirmationDialog(request);
        Assert.Equal(["\u202D" + line + "\u202C", "\u202Dplain\u202C"], dialog.ShownCode);
        Assert.Equal([line, "plain"], dialog.Code);
        Assert.Equal(request, Question("t", code: [line, "plain"]));
        Assert.Equal(dialog, new ConfirmationDialog(Question("t", code: [line, "plain"])));
        // A line can hold neither, so none can end the wrapper early.
        Assert.Equal(@"\u{202D}x\u{202C}", ControlText.Escaped("\u202Dx\u202C"));
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

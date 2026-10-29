using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ConnectorControl.App.Tests.TestSupport;
using ConnectorControl.App.Views;
using ConnectorControl.Core;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.App.Tests;

/// <summary>Windows only: the Mac draws the same question in Sources/ConnectorControl/ConfirmationAccessory.swift, which
/// its app target leaves untested, and proves the wait in Tests/ConnectorControlStateTests/DialogConfirmerRunLoopTests.swift.</summary>
public class ConfirmationDialogTests
{
    private static readonly ConfirmationRequest Change = new(ControlTools.ChangeTitle("Claude", "splunk"),
        ControlTools.ChangeActiveMessage("Harvard"), ["url", "  − https://a.example/mcp", "  + https://b.example/mcp"],
        ControlTools.TakesEffectFooter);

    private static string Text(InlineCollection inlines) => string.Concat(inlines.Select(inline => inline switch
    {
        Run run => run.Text,
        LineBreak => "\n",
        _ => "",
    }));

    /// <summary>What would run, as the box holds it: a paragraph a line.</summary>
    private static List<Paragraph> Lines(RichTextBox box) => box.Document.Blocks.Cast<Paragraph>().ToList();

    private static string Code(RichTextBox box) => string.Join("\n", Lines(box).Select(line => Text(line.Inlines)));

    /// <summary>Where each line of what would run starts and ends, as the box has laid it out: the rect of its first
    /// character and of the end of its last.</summary>
    private static List<(Rect Start, Rect End)> DrawnLines(RichTextBox box) =>
        Lines(box).Select(line => (line.ContentStart.GetCharacterRect(LogicalDirection.Forward),
            line.ContentEnd.GetCharacterRect(LogicalDirection.Backward))).ToList();

    /// <summary>The confirmation on screen, if one is: a window joins the application's list when it is built and
    /// leaves it only when closed, so a dialog another test built and never showed is listed too, and is not it.</summary>
    private static ConfirmDialog? Shown() => Application.Current.Windows.OfType<ConfirmDialog>().FirstOrDefault(window => window.IsVisible);

    /// <summary>Shows <paramref name="dialog"/> off screen, modeless and not activated, and runs the dispatcher until it
    /// is idle, so layout, and any text formatting left for the background, has finished: its text is laid out
    /// and its box scrolls as the app's do.</summary>
    private static void ShowOffScreen(ConfirmDialog dialog)
    {
        dialog.Left = FlyoutWindow.OffScreen;
        dialog.Top = FlyoutWindow.OffScreen;
        dialog.ShowActivated = false;
        dialog.Show();
        dialog.UpdateLayout();
        dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    [Fact]
    public void AChangeSetsNamesApartAndShowsWhatWouldRun()
    {
        WpfApp.Invoke(() =>
        {
            var dialog = new ConfirmDialog(new ConfirmationDialog(Change));
            var splunk = new TextRun("splunk", IsSetApart: true).ShownText;
            Assert.Equal($"Claude wants to change “{splunk}”", Text(dialog.MessageText.Inlines));
            var mono = ((FontFamily)dialog.FindResource("MonoFont")).Source;
            Assert.Equal(mono, dialog.MessageText.Inlines.OfType<Run>().Single(run => run.Text == splunk).FontFamily.Source);
            Assert.NotEqual(mono, dialog.MessageText.Inlines.OfType<Run>().First().FontFamily.Source);
            Assert.Equal($"In “{new TextRun("Harvard", IsSetApart: true).ShownText}”, the collection Claude is running:",
                Text(dialog.InformativeText.Inlines));
            // Each line drawn in the order it is stored, between a left-to-right override and its pop.
            Assert.Equal("\u202Durl\u202C\n\u202D  − https://a.example/mcp\u202C\n\u202D  + https://b.example/mcp\u202C", Code(dialog.CodeText));
            // A paragraph a line, as the Mac lays them out, each its own left-to-right paragraph and none spaced
            // from the next.
            Assert.All(Lines(dialog.CodeText), line => Assert.Equal(new Thickness(0), line.Margin));
            Assert.True(dialog.CodeText.IsReadOnly);
            Assert.Equal(Visibility.Visible, dialog.CodeText.Visibility);
            Assert.Equal(ControlTools.TakesEffectFooter, Text(dialog.FooterText.Inlines));
            Assert.Equal(Visibility.Visible, dialog.FooterText.Visibility);
            Assert.Equal(ConfirmationDialog.DontAllowButton, dialog.CancelButton.Content);
            Assert.Equal(ConfirmationDialog.AllowButton, dialog.PrimaryButton.Content);
            Assert.True(dialog.CancelButton.IsDefault);
            Assert.True(dialog.CancelButton.IsCancel);
            Assert.False(dialog.PrimaryButton.IsDefault);
            // Return answers Don’t Allow wherever the keyboard is: Allow, focused with Tab or by a press dragged
            // off it, does not take Return itself, as a focused button otherwise would.
            Assert.False(KeyboardNavigation.GetAcceptsReturn(dialog.PrimaryButton));
            // The keyboard starts on Don’t Allow, as it does on the Mac.
            Assert.Same(dialog.CancelButton, FocusManager.GetFocusedElement(dialog));
            Assert.All(new FrameworkElement[] { dialog.MessageText, dialog.InformativeText, dialog.CodeText, dialog.FooterText },
                element => Assert.Equal(FlowDirection.LeftToRight, element.FlowDirection));
            Assert.False(dialog.Result);
            dialog.Close();
        });
    }

    [Fact]
    public void AQuestionWithNothingToRunHidesTheCodeAndTheFooter()
    {
        WpfApp.Invoke(() =>
        {
            var dialog = new ConfirmDialog(new ConfirmationDialog(
                new ConfirmationRequest(ControlTools.RestartItselfTitle, ControlTools.RestartMessage, [], null)));
            Assert.Equal(Visibility.Collapsed, dialog.CodeText.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.FooterText.Visibility);
            Assert.Equal(ControlTools.RestartMessage, Text(dialog.InformativeText.Inlines));
            dialog.Close();
        });
    }

    /// <summary>A removed line is drawn in the theme's critical colour and an added one in its success colour; a
    /// theme without one leaves the line in the box's own colour, and its "−" or "+" still says what it does.</summary>
    [Fact]
    public void RemovedAndAddedLinesTakeTheThemesColours()
    {
        WpfApp.Invoke(() =>
        {
            var dialog = new ConfirmDialog(new ConfirmationDialog(Change));
            var runs = Lines(dialog.CodeText).Select(line => line.Inlines.OfType<Run>().Single()).ToList();
            Assert.Equal(DependencyProperty.UnsetValue, runs[0].ReadLocalValue(TextElement.ForegroundProperty));
            Assert.Same(dialog.TryFindResource("SystemFillColorCriticalBrush") ?? runs[0].Foreground, runs[1].Foreground);
            Assert.Same(dialog.TryFindResource("SystemFillColorSuccessBrush") ?? runs[0].Foreground, runs[2].Foreground);
            dialog.Close();
        });
    }

    /// <summary>A line far wider than the dialog is still one line, and the box scrolls sideways to it; with nothing
    /// that wide there is nothing to scroll. The wide lines are as long as the tools let a line be: one of
    /// ASCII words, with spaces to break at, the widest; one of scalars that each take two UTF-16 code units,
    /// the longest in the units at which WPF breaks a line whatever the page's width.
    ///
    /// Measured on the real layout: the dialog is shown off screen, so the box lays out and scrolls its text as
    /// the app's does, and each line's first character and its end are found where that layout put them. A
    /// line that wrapped ends on a lower line than it starts on, so its end's top is at or below its start's
    /// bottom; one that did not ends level with its start and, when it is wide, far to the right of the box. No
    /// layout gives empty rects, which fail here rather than pass.</summary>
    [Fact]
    public void ALineNeverWrapsAndOnlyAWideOneScrolls()
    {
        var words = string.Concat(Enumerable.Repeat("--flag=value ", ControlTools.ShownLineLimit / 13 + 1))[..ControlTools.ShownLineLimit];
        var pairs = string.Concat(Enumerable.Repeat("\U0001D431", ControlTools.ShownLineLimit));
        Assert.Equal(ControlTools.ShownLineLimit, ControlText.Width(words));
        Assert.Equal(ControlTools.ShownLineLimit, ControlText.Width(pairs));
        Assert.Equal(2 * ControlTools.ShownLineLimit, pairs.Length);
        var wide = new ConfirmationRequest(ControlTools.ChangeTitle("Claude", "splunk"), ControlTools.ChangeActiveMessage("Harvard"),
            ["command  npx", words, pairs, "env      TOKEN = ••• (set)"], null);
        WpfApp.Invoke(() =>
        {
            foreach (var (request, isWide) in new[] { (Change, false), (wide, true) })
            {
                var dialog = new ConfirmDialog(new ConfirmationDialog(request));
                try
                {
                    ShowOffScreen(dialog);
                    var box = dialog.CodeText;
                    var drawn = DrawnLines(box);
                    Assert.Equal(request.Code.Count, drawn.Count);
                    for (var index = 0; index < drawn.Count; index++)
                    {
                        var (start, end) = drawn[index];
                        Assert.False(start.IsEmpty);
                        Assert.False(end.IsEmpty);
                        Assert.True(end.Top < start.Bottom, $"line {index} wrapped: it starts at {start} and ends at {end}");
                        if (index > 0)
                        {
                            Assert.True(start.Top > drawn[index - 1].Start.Top);
                        }
                        if (request.Code[index].Length > 100)
                        {
                            Assert.True(end.Left > box.ViewportWidth);
                        }
                    }
                    Assert.True(box.ViewportWidth > 0);
                    Assert.Equal(isWide, box.ExtentWidth > box.ViewportWidth);
                }
                finally
                {
                    dialog.Close();
                }
            }
        });
    }

    /// <summary>Closing the window without a button is Don’t Allow.</summary>
    [Fact]
    public void ClosingTheDialogIsDontAllow()
    {
        WpfApp.Invoke(() =>
        {
            var closer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            closer.Tick += (_, _) =>
            {
                if (Shown() is { } open)
                {
                    closer.Stop();
                    open.Close();
                }
            };
            closer.Start();
            try
            {
                Assert.False(new WpfDialogs(() => null).ConfirmChange(new ConfirmationDialog(Change)));
            }
            finally
            {
                closer.Stop();
            }
        });
    }

    /// <summary>A failure while the dialog is up comes out of ShowDialog, for the confirmer to answer Don’t Allow,
    /// and takes the dialog down with it; one before it shows closes it too, so it leaves no window behind.
    /// Shown modelessly and failed at once, so nothing is thrown through the dispatcher every test here shares.</summary>
    [Fact]
    public void AFailureWhileTheDialogIsUpClosesIt()
    {
        WpfApp.Invoke(() =>
        {
            var dialog = new ConfirmDialog(new ConfirmationDialog(Change)) { Left = FlyoutWindow.OffScreen, Top = FlyoutWindow.OffScreen };
            var failure = Record.Exception(() => ConfirmDialog.ClosedOnFailure(dialog, () =>
            {
                dialog.Show();
                throw new InvalidOperationException("drawing failed");
            }));
            Assert.Equal("drawing failed", Assert.IsType<InvalidOperationException>(failure).Message);
            Assert.False(dialog.IsVisible);
            Assert.DoesNotContain(dialog, Application.Current.Windows.OfType<ConfirmDialog>());

            var unshown = new ConfirmDialog(new ConfirmationDialog(Change));
            Assert.Contains(unshown, Application.Current.Windows.OfType<ConfirmDialog>());
            var early = Record.Exception(() => ConfirmDialog.ClosedOnFailure(unshown, () => throw new InvalidOperationException("no owner")));
            Assert.Equal("no owner", Assert.IsType<InvalidOperationException>(early).Message);
            Assert.DoesNotContain(unshown, Application.Current.Windows.OfType<ConfirmDialog>());
        });
    }

    /// <summary>The dialog is modal: ShowDialog holds the UI thread in a nested frame until the user answers. The
    /// call arrives as a line from the pipe does, posted to the UI thread; the tools' delays are dispatcher
    /// timers a thousand times faster; Allow is clicked once the caller has had its early answer. WPF's nested
    /// frame runs the dispatcher's queue whatever called ShowDialog, so this holds with an inline presenter too —
    /// the Mac's counterpart is the one a presenter can break. A nested frame keeps the pump below from
    /// checking its own deadline, so the clicker gives up on the early answer at one of its own and closes the
    /// dialog, Don’t Allow, which ends the test on its asserts rather than leaving it hung.</summary>
    [Fact]
    public void TheCallersWaitGoesOnWhileTheDialogIsOpen()
    {
        using var rig = new ControlRig();
        var answer = new ControlRig.Answer();
        WpfApp.Invoke(() =>
        {
            var host = new AppHost(UiThread.Marshal, (delay, action) => UiThread.Delay(delay / 1000, action),
                () => DateTime.UtcNow, UiThread.Background);
            var tools = new ControlTools(rig.State, rig.SettingsModel, new DialogConfirmer(new WpfDialogs(() => null), UiThread.Post), host);
            var givesUp = DateTime.UtcNow + Wait.Eventually;
            var clicker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            clicker.Tick += (_, _) =>
            {
                if (Shown() is not { } open)
                {
                    return;
                }
                if (answer.Result is not null)
                {
                    clicker.Stop();
                    open.PrimaryButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }
                else if (DateTime.UtcNow >= givesUp)
                {
                    clicker.Stop();
                    open.Close();
                }
            };
            clicker.Start();
            try
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() => tools.Call("add_connector",
                    JsonValue.Object(("name", JsonValue.String("x")), ("config", JsonValue.Object(("command", JsonValue.String("npx"))))),
                    new ControlCaller.Relay("claude-ai"), answer.Progress.Add, result =>
                    {
                        answer.Result = result;
                        answer.Completions++;
                    })));
                Assert.True(PumpUntil(() => rig.State.Store.Mcps.ContainsKey("x") && Shown() is null, Wait.Eventually));
            }
            finally
            {
                clicker.Stop();
            }
        });
        Assert.Equal([5.0, 10, 15, 20, 25, 30, 35, 40, 45, 50], answer.Progress);
        Assert.Equal(ControlTools.WaitingResult, answer.Result);
        Assert.Equal(1, answer.Completions);
        Assert.Equal([ControlTools.AddedNotification("Claude", "x", "Default")], rig.H.Notifier.Sent.Select(sent => sent.Body));
    }

    /// <summary>Runs the dispatcher until <paramref name="condition"/> holds, a frame at a time, so a dialog it opens
    /// runs its own nested frame inside one.</summary>
    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        return true;
    }
}

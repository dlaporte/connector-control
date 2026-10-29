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

    /// <summary>How many lines the box draws, once laid out: one a line of what would run, unless one wraps. None
    /// before layout, which no assert here takes for a count.</summary>
    private static int DrawnLines(RichTextBox box)
    {
        var count = 0;
        for (var line = box.Document.ContentStart.GetLineStartPosition(0); line is not null; line = line.GetLineStartPosition(1))
        {
            count++;
        }
        return count;
    }

    /// <summary>The confirmation on screen, if one is: a window joins the application's list when it is built and
    /// leaves it only when closed, so a dialog another test built and never showed is listed too, and is not it.</summary>
    private static ConfirmDialog? Shown() => Application.Current.Windows.OfType<ConfirmDialog>().FirstOrDefault(window => window.IsVisible);

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
            Assert.Equal("url\n  − https://a.example/mcp\n  + https://b.example/mcp", Code(dialog.CodeText));
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

    /// <summary>A line far wider than the dialog — a long command line, a key's whole value — is still one line,
    /// and the box scrolls sideways to it; with nothing that wide there is nothing to scroll. The wide line has
    /// spaces to break at, so a page any narrower than it would wrap it.</summary>
    [Fact]
    public void ALineNeverWrapsAndOnlyAWideOneScrolls()
    {
        WpfApp.Invoke(() =>
        {
            var narrow = new ConfirmDialog(new ConfirmationDialog(Change));
            WindowTestSupport.Layout(narrow, new Size(440, 1000));
            Assert.Equal(Change.Code.Count, DrawnLines(narrow.CodeText));
            Assert.True(narrow.CodeText.ViewportWidth > 0);
            Assert.True(narrow.CodeText.ExtentWidth <= narrow.CodeText.ViewportWidth);
            narrow.Close();

            var longLine = "args     " + string.Join(" ", Enumerable.Repeat("--flag=value", 170));
            Assert.True(longLine.Length > 2000);
            var request = new ConfirmationRequest(ControlTools.ChangeTitle("Claude", "splunk"), ControlTools.ChangeActiveMessage("Harvard"),
                ["command  npx", longLine, "env      TOKEN = ••• (set)"], null);
            var wide = new ConfirmDialog(new ConfirmationDialog(request));
            WindowTestSupport.Layout(wide, new Size(440, 1000));
            Assert.Equal(request.Code.Count, DrawnLines(wide.CodeText));
            Assert.True(wide.CodeText.ExtentWidth > wide.CodeText.ViewportWidth);
            wide.Close();
        });
    }

    /// <summary>A line wider than any page WPF lays out could only wrap, so a question holding one is not drawn: the
    /// dialog fails before it shows, and the confirmer answers Don’t Allow.</summary>
    [Fact]
    public void ALineTooWideForAnyPageIsNotDrawn()
    {
        WpfApp.Invoke(() =>
        {
            var request = new ConfirmationRequest(ControlTools.ChangeTitle("Claude", "splunk"), ControlTools.ChangeActiveMessage("Harvard"),
                ["args     " + string.Join(" ", Enumerable.Repeat("--flag=value", 15_000)), "env      SAFE = 1"], null);
            Assert.Throws<InvalidOperationException>(() => new ConfirmDialog(new ConfirmationDialog(request)));
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

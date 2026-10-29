using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    /// <summary>What would run, as the dialog holds it: a read-only text box a line.</summary>
    private static List<TextBox> Lines(ConfirmDialog dialog) => dialog.CodeLines.Children.OfType<TextBox>().ToList();

    private static string Code(ConfirmDialog dialog) => string.Join("\n", Lines(dialog).Select(line => line.Text));

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
            Assert.Equal("url\n  − https://a.example/mcp\n  + https://b.example/mcp", Code(dialog));
            // A box a line, as the Mac lays them out, each read-only but selectable, left to right, and none
            // spaced from the next.
            Assert.All(Lines(dialog), line =>
            {
                Assert.True(line.IsReadOnly);
                Assert.Equal(new Thickness(0), line.Margin);
                Assert.Equal(FlowDirection.LeftToRight, line.FlowDirection);
                Assert.Equal(TextWrapping.NoWrap, line.TextWrapping);
            });
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

    /// <summary>A line that holds a right-to-left letter is drawn between a left-to-right override and its pop, so it
    /// is drawn in the order it is stored; a line with none is drawn as it is.</summary>
    [Fact]
    public void ARightToLeftLineIsDrawnInTheOrderItIsStored()
    {
        var line = "--ports \u0627 80 443 22";
        WpfApp.Invoke(() =>
        {
            var dialog = new ConfirmDialog(new ConfirmationDialog(new ConfirmationRequest(ControlTools.ChangeTitle("Claude", "splunk"),
                ControlTools.ChangeActiveMessage("Harvard"), ["url", line], null)));
            Assert.Equal("url\n\u202D" + line + "\u202C", Code(dialog));
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
            var lines = Lines(dialog);
            Assert.Equal(DependencyProperty.UnsetValue, lines[0].ReadLocalValue(Control.ForegroundProperty));
            Assert.Same(dialog.TryFindResource("SystemFillColorCriticalBrush") ?? lines[0].Foreground, lines[1].Foreground);
            Assert.Same(dialog.TryFindResource("SystemFillColorSuccessBrush") ?? lines[0].Foreground, lines[2].Foreground);
            dialog.Close();
        });
    }

    /// <summary>A line far wider than the dialog is still one line, and the box scrolls sideways to it; with nothing
    /// that wide there is nothing to scroll. The wide lines are as long as the tools let a line be: one of
    /// ASCII words, with spaces to break at, the widest; one of scalars that each take two UTF-16 code units,
    /// the longest in the units at which WPF breaks a line whatever its width.
    ///
    /// Measured on the real layout: the dialog is shown off screen, so the box lays out and scrolls its lines as
    /// the app's does. Each line keeps to one line of text, one under the other, and a wide one runs far past
    /// the box's right edge.</summary>
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
                    var scroll = dialog.CodeScroll;
                    var lines = Lines(dialog);
                    Assert.Equal(request.Code.Count, lines.Count);
                    for (var index = 0; index < lines.Count; index++)
                    {
                        Assert.Equal(1, lines[index].LineCount);
                        if (index > 0)
                        {
                            Assert.True(Top(lines[index], dialog) > Top(lines[index - 1], dialog));
                        }
                        if (request.Code[index].Length > 100)
                        {
                            Assert.True(lines[index].ActualWidth > scroll.ViewportWidth);
                        }
                    }
                    Assert.True(scroll.ViewportWidth > 0);
                    Assert.Equal(isWide, scroll.ExtentWidth > scroll.ViewportWidth);
                }
                finally
                {
                    dialog.Close();
                }
            }
        });
    }

    /// <summary>Where <paramref name="line"/>'s top is, in the lines' own panel.</summary>
    private static double Top(TextBox line, ConfirmDialog dialog) => line.TranslatePoint(new Point(0, 0), dialog.CodeLines).Y;

    /// <summary>Each line of what would run is clipped to a box one line tall, and the boxes touch without
    /// overlapping, so no combining marks stacked on a line, however tall, can draw over the line above or below
    /// it. A line's height comes from the face alone: one with marks is as tall as one without.</summary>
    [Fact]
    public void EachLineIsClippedToItsOwnBox()
    {
        var request = new ConfirmationRequest(ControlTools.ChangeTitle("Claude", "splunk"), ControlTools.ChangeActiveMessage("Harvard"),
            ["url", "  + l\u1DD8\u1DD8", "  + g\u034D\u034D", "  + NODE_OPTIONS = --require=/tmp/evil.js"], null);
        WpfApp.Invoke(() =>
        {
            var dialog = new ConfirmDialog(new ConfirmationDialog(request));
            try
            {
                ShowOffScreen(dialog);
                var lines = Lines(dialog);
                Assert.Equal(request.Code.Count, lines.Count);
                var height = lines[0].ActualHeight;
                Assert.True(height > 0);
                for (var index = 0; index < lines.Count; index++)
                {
                    Assert.True(lines[index].ClipToBounds, $"line {index} is clipped to its own box");
                    Assert.Equal(height, lines[index].ActualHeight);
                    Assert.Equal(Top(lines[0], dialog) + index * height, Top(lines[index], dialog), 3);
                }
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    /// <summary>A line's box is as tall as the code face needs, and grows only for the letters of another script drawn
    /// in a taller fallback face: never for marks, and never past half a line more above and below. The room is
    /// the box's padding, so the line's text sits clear of the box's edges.</summary>
    [Fact]
    public void ALineBoxGrowsForTallerScriptsButNotForMarks()
    {
        WpfApp.Invoke(() =>
        {
            double Height(params string[] code)
            {
                var dialog = new ConfirmDialog(new ConfirmationDialog(new ConfirmationRequest("t", "", code, null)));
                try
                {
                    ShowOffScreen(dialog);
                    return Lines(dialog)[0].ActualHeight;
                }
                finally
                {
                    dialog.Close();
                }
            }
            var plain = Height("url", "plain");
            Assert.True(plain > 0);
            Assert.Equal(plain, Height("url", "  + l\u1DD8\u1DD8", "  + g\u034D\u034D")); // marks never make a line taller
            var tall = Height("  + \u0939\u093F\u0928\u094D\u0926\u0940");
            Assert.True(tall > plain, "Devanagari's fallback face needs more room than the code face");
            Assert.True(tall <= plain + 2 * Math.Ceiling(plain / 2), "bounded");
        });
    }

    /// <summary>Ordinary text in scripts the code face does not have — Chinese, Arabic, Devanagari, Thai — is drawn whole:
    /// its ink stays clear of its box's edges. A line of stacked marks is cut off by its own box, and the line
    /// beside it stays blank.</summary>
    [Fact]
    public void OrdinaryTextInOtherScriptsIsNotClippedAndMarksNeverReachTheNextLine()
    {
        var stack = "  + l\u1DD8\u1DD8 g\u034D\u034D";
        var request = new ConfirmationRequest("t", "", ["  + \u6F22\u5B57", "  + \u0627\u0644\u0639\u0631\u0628\u064A\u0629",
            "  + \u0939\u093F\u0928\u094D\u0926\u0940", "  + \u0E17\u0E35\u0E48", stack, " ", stack], null);
        WpfApp.Invoke(() =>
        {
            var dialog = new ConfirmDialog(new ConfirmationDialog(request));
            try
            {
                ShowOffScreen(dialog);
                var lines = Lines(dialog);
                Assert.Equal(7, lines.Count);
                for (var index = 0; index < 4; index++)
                {
                    var rows = InkRows(lines[index]);
                    Assert.NotEmpty(rows);
                    Assert.True(rows.Min() > 0, $"line {index}'s ink clears its box's top");
                    Assert.True(rows.Max() < lines[index].ActualHeight - 1, $"line {index}'s ink clears its box's bottom");
                }
                // The blank line between two stacks, drawn as the panel draws it, holds no ink.
                var panel = InkRowsOf(dialog.CodeLines);
                var top = Top(lines[5], dialog);
                Assert.DoesNotContain(panel, row => row >= top && row < top + lines[5].ActualHeight);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    /// <summary>The rows of <paramref name="line"/>, in its own device-independent pixels, that hold any ink.</summary>
    private static List<double> InkRows(FrameworkElement line) => InkRowsOf(line);

    /// <summary>The rows of <paramref name="element"/>, as drawn with its clip, that hold any ink.</summary>
    private static List<double> InkRowsOf(FrameworkElement element)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var rows = new List<double>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (pixels[(y * width + x) * 4 + 3] > 12)
                {
                    rows.Add(y / dpi.DpiScaleY);
                    break;
                }
            }
        }
        return rows;
    }

    /// <summary>The block is one stop for Tab, as one text box was, and Up and Down move the keyboard to the line above
    /// or below, Page Up and Page Down a block's height of lines, each scrolling the line it reaches into view. A
    /// line takes neither Return nor Tab, so Return still answers Don’t Allow.</summary>
    [Fact]
    public void TheBlockIsOneTabStopAndUpAndDownMoveBetweenLines()
    {
        var request = new ConfirmationRequest("t", "", Enumerable.Range(0, 40).Select(index => $"line {index}").ToList(), null);
        WpfApp.Invoke(() =>
        {
            var dialog = new ConfirmDialog(new ConfirmationDialog(request));
            try
            {
                ShowOffScreen(dialog);
                Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(dialog.CodeLines));
                var lines = Lines(dialog);
                Assert.All(lines, line =>
                {
                    Assert.True(line.IsTabStop);
                    Assert.False(line.AcceptsReturn);
                    Assert.False(line.AcceptsTab);
                });
                // The keyboard still starts on Don’t Allow.
                Assert.Same(dialog.CancelButton, FocusManager.GetFocusedElement(dialog));
                lines[0].Focus();
                Press(lines[0], Key.Down);
                Assert.Same(lines[1], FocusManager.GetFocusedElement(dialog));
                Press(lines[1], Key.Up);
                Assert.Same(lines[0], FocusManager.GetFocusedElement(dialog));
                Press(lines[0], Key.Up);
                Assert.Same(lines[0], FocusManager.GetFocusedElement(dialog)); // the first line stays put
                Press(lines[0], Key.PageDown);
                var paged = Assert.IsType<TextBox>(FocusManager.GetFocusedElement(dialog));
                var index = lines.IndexOf(paged);
                Assert.True(index > 1, "a page is more than a line");
                dialog.UpdateLayout();
                var top = paged.TranslatePoint(new Point(0, 0), dialog.CodeScroll).Y;
                Assert.True(top >= 0 && top + paged.ActualHeight <= dialog.CodeScroll.ViewportHeight + 1,
                    "the line the keyboard moved to is scrolled into view");
                for (var step = 0; step < 60; step++)
                {
                    Press((TextBox)FocusManager.GetFocusedElement(dialog), Key.Down);
                }
                Assert.Same(lines[^1], FocusManager.GetFocusedElement(dialog)); // the last line stays put
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    /// <summary>A key pressed on <paramref name="line"/>, as the keyboard sends it first.</summary>
    private static void Press(TextBox line, Key key) =>
        line.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(line)!, 0, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });

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

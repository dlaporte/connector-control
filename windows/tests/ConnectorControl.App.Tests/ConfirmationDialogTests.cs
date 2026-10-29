using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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

    private static string Code(RichTextBox box) => Text(((Paragraph)box.Document.Blocks.FirstBlock).Inlines);

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
            Assert.Equal(ConfirmDialog.UnwrappedWidth, dialog.CodeText.Document.PageWidth);
            Assert.True(dialog.CodeText.IsReadOnly);
            Assert.Equal(Visibility.Visible, dialog.CodeText.Visibility);
            Assert.Equal(ControlTools.TakesEffectFooter, Text(dialog.FooterText.Inlines));
            Assert.Equal(Visibility.Visible, dialog.FooterText.Visibility);
            Assert.Equal(ConfirmationDialog.DontAllowButton, dialog.CancelButton.Content);
            Assert.Equal(ConfirmationDialog.AllowButton, dialog.PrimaryButton.Content);
            Assert.True(dialog.CancelButton.IsDefault);
            Assert.True(dialog.CancelButton.IsCancel);
            Assert.False(dialog.PrimaryButton.IsDefault);
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
    /// and takes the dialog down with it. Shown modelessly and failed at once, so nothing is thrown through
    /// the dispatcher every test here shares.</summary>
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

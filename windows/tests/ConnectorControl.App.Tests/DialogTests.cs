using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using ConnectorControl.App.Services;
using ConnectorControl.App.Tests.TestSupport;
using ConnectorControl.App.Views;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.App.Tests;

public class DialogTests
{
    [Fact]
    public void ConfirmDialogShowsTheMacTextsAndButtons()
    {
        WpfApp.Invoke(() =>
        {
            var dialog = new ConfirmDialog("Restart Claude Desktop now?", "Any in-progress Claude conversation will be interrupted.", "Restart", "Cancel", destructive: false);
            Assert.Equal("Restart Claude Desktop now?", dialog.MessageText.Text);
            Assert.Equal("Any in-progress Claude conversation will be interrupted.", dialog.InformativeText.Text);
            Assert.Equal(Visibility.Visible, dialog.InformativeText.Visibility);
            Assert.Equal("Restart", dialog.PrimaryButton.Content);
            Assert.Equal("Cancel", dialog.CancelButton.Content);
            Assert.True(dialog.PrimaryButton.IsDefault);
            Assert.True(dialog.CancelButton.IsCancel);
            Assert.False(dialog.Result);
        });
    }

    [Fact]
    public void AConfirmDialogCanMakeItsCancelButtonTheDefault()
    {
        WpfApp.Invoke(() =>
        {
            // The published-file question: Return and Escape both answer Keep, the accent goes with
            // the default, and Delete keeps its red but answers only a click.
            var dialog = new ConfirmDialog(CollectionsModel.DeletePublishedFileQuestion("team.json"), null,
                CollectionsModel.DeleteFileButton, CollectionsModel.KeepFileButton, destructive: true, cancelIsDefault: true);
            Assert.True(dialog.CancelButton.IsDefault);
            Assert.True(dialog.CancelButton.IsCancel);
            Assert.False(dialog.PrimaryButton.IsDefault);
            Assert.Same(dialog.TryFindResource("DestructiveButton"), dialog.PrimaryButton.Style);
            Assert.Same(dialog.TryFindResource("AccentButtonStyle"), dialog.CancelButton.Style);
            // Even with the keyboard on Delete: a focused button would otherwise take Return itself.
            Assert.False(KeyboardNavigation.GetAcceptsReturn(dialog.PrimaryButton));
            // The keyboard starts on Keep, as it does on the Mac.
            Assert.Same(dialog.CancelButton, FocusManager.GetFocusedElement(dialog));
            // Every other question keeps Return on its primary.
            var usual = new ConfirmDialog("Restart Claude Desktop now?", null, "Restart", "Cancel", destructive: false);
            Assert.True(usual.PrimaryButton.IsDefault);
            Assert.False(usual.CancelButton.IsDefault);
        });
    }

    [Fact]
    public void ConfirmDialogWithoutInformativeTextOrCancelHidesThem()
    {
        WpfApp.Invoke(() =>
        {
            var dialog = new ConfirmDialog("You're up to date.", null, "OK", null, destructive: false);
            Assert.Equal(Visibility.Collapsed, dialog.InformativeText.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.CancelButton.Visibility);
            // Delete Collection's own question, informative and button, as CollectionsModel.Delete asks it.
            var informative = string.Join(" ", CollectionsModel.DeleteConnectorsSentence(2), CollectionsModel.DeleteCopiesSentence,
                CollectionsModel.DeleteCheckedInformative);
            var destructive = new ConfirmDialog(AppState.DeleteCollectionMessage("Work"), informative, AppState.DeleteButton, "Cancel", destructive: true);
            Assert.Same(destructive.TryFindResource("DestructiveButton"), destructive.PrimaryButton.Style);
        });
    }

    [Fact]
    public void NamePromptDialogPrefillsAndSelectsTheText()
    {
        WpfApp.Invoke(() =>
        {
            var dialog = new NamePromptDialog("Rename Collection", "Default");
            Assert.Equal("Rename Collection", dialog.Title);
            Assert.Equal("Default", dialog.NameBox.Text);
            Assert.Null(dialog.Result);
            // The field is named by the prompt's title, its label.
            WindowTestSupport.Layout(dialog, new Size(320, 200));   // the ElementName binding resolves with the first binding pass
            Assert.Same(dialog.TitleText, AutomationProperties.GetLabeledBy(dialog.NameBox));
        });
    }

    [Fact]
    public void UpdateDialogRendersTheReleaseNotes()
    {
        WpfApp.Invoke(() =>
        {
            var dialog = new UpdateDialog("1.3.0", "1.2.2", "## Fixes\n- one\n- two", _ => Task.CompletedTask);
            Assert.Equal("Software Update", dialog.Title);
            Assert.Equal(UpdateCoordinator.AvailableHeadline, dialog.Headline.Text);
            Assert.Equal("Connector Control 1.3.0 is now available - you have 1.2.2. Would you like to install it now?", dialog.Detail.Text);
            Assert.NotNull(dialog.Notes.Document);
            Assert.True(dialog.Notes.Document.Blocks.Count >= 2);   // a heading and a list
            Assert.Equal("Install and Relaunch", dialog.InstallButton.Content);
            Assert.Equal("Later", dialog.LaterButton.Content);

            // Install and Relaunch's busy state: Install and Relaunch off and reading Downloading…, Later reading Cancel.
            dialog.SetBusy(true);
            Assert.Equal(UpdateCoordinator.DownloadingTitle, dialog.InstallButton.Content);
            Assert.False(dialog.InstallButton.IsEnabled);
            Assert.Equal("Cancel", dialog.LaterButton.Content);
            Assert.True(dialog.LaterButton.IsEnabled);
            dialog.SetBusy(false);
            Assert.Equal("Install and Relaunch", dialog.InstallButton.Content);
            Assert.Equal("Later", dialog.LaterButton.Content);
            Assert.True(dialog.LaterButton.IsEnabled);
        });
    }

    /// <summary>Install and Relaunch keeps the dialog up, busy, until the install ends, and a close meanwhile (the title
    /// bar's, or Alt+F4) is refused: closed, it would answer Later while the download ran on. Then it closes, answering
    /// Install and Relaunch.</summary>
    [Fact]
    public void InstallKeepsTheDialogUpUntilItEndsAndACloseMeanwhileIsRefused()
    {
        WpfApp.Invoke(() =>
        {
            var finish = new TaskCompletionSource();
            var dialog = new UpdateDialog("1.3.0", "1.2.2", null, _ => finish.Task);
            dialog.Show();
            try
            {
                Click(dialog.InstallButton);
                Assert.Equal(UpdateCoordinator.DownloadingTitle, dialog.InstallButton.Content);
                Assert.False(dialog.InstallButton.IsEnabled);

                dialog.Close();
                Assert.True(dialog.IsVisible, "a close while the install runs is refused");

                finish.SetResult();
                Assert.True(PumpUntil(() => !dialog.IsVisible, TimeSpan.FromSeconds(10)), "the dialog closes once the install ends");
                Assert.True(dialog.Result);
            }
            finally
            {
                finish.TrySetResult();
                PumpUntil(() => !dialog.IsVisible, TimeSpan.FromSeconds(5));
            }
        });
    }

    /// <summary>Cancel, which Later reads while busy, cancels the token the install was given; once the install has
    /// stopped the dialog is back at its offer, still up, where Later then answers it.</summary>
    [Fact]
    public void CancelWhileDownloadingStopsTheInstallAndReturnsToTheOffer()
    {
        WpfApp.Invoke(() =>
        {
            CancellationToken given = default;
            var dialog = new UpdateDialog("1.3.0", "1.2.2", null, async token =>
            {
                given = token;
                var stopped = new TaskCompletionSource();
                using (token.Register(() => stopped.TrySetResult()))
                {
                    await stopped.Task;
                }
            });
            dialog.Show();
            try
            {
                Click(dialog.InstallButton);
                Assert.Equal("Cancel", dialog.LaterButton.Content);

                // Pressed as Narrator or a click would: through the button's own OnClick, which a disabled button refuses.
                Press(dialog.LaterButton);
                Assert.True(PumpUntil(() => given.IsCancellationRequested, TimeSpan.FromSeconds(10)), "Cancel cancels the install's token");
                Assert.True(PumpUntil(() => dialog.InstallButton.IsEnabled, TimeSpan.FromSeconds(10)), "the dialog leaves its busy state");
                Assert.True(dialog.IsVisible, "a cancelled install leaves the dialog up");
                Assert.False(dialog.Result);
                Assert.Equal("Install and Relaunch", dialog.InstallButton.Content);
                Assert.Equal("Later", dialog.LaterButton.Content);

                Click(dialog.LaterButton);
                Assert.False(dialog.IsVisible);
                Assert.False(dialog.Result);
            }
            finally
            {
                if (dialog.IsVisible)
                {
                    if (!dialog.InstallButton.IsEnabled)
                    {
                        Click(dialog.LaterButton);
                        PumpUntil(() => dialog.InstallButton.IsEnabled, TimeSpan.FromSeconds(5));
                    }
                    dialog.Close();
                }
            }
        });
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    /// <summary>Presses <paramref name="button"/> through its automation peer, which runs ButtonBase.OnClick (posted to the dispatcher).</summary>
    private static void Press(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();

    /// <summary>Runs the dispatcher a frame at a time until <paramref name="condition"/> holds: where the dialog's awaits resume.</summary>
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

    [Fact]
    public void UpdateNotesNeverLoadAnImage()
    {
        // The notes come from the unsigned release feed and are shown before the package is checked: an
        // image would be fetched as the dialog opens - an http beacon, or a file:// share that hands
        // Windows' sign-in to whoever runs it. Images show as their alt text, and nothing is loaded.
        WpfApp.Invoke(() =>
        {
            const string notes = "Fixes.\n\n![beacon](https://example.invalid/b.png) ![share](file://host.invalid/share/x.png)\n\n"
                + "[![nested](file://host.invalid/share/y.png)](https://example.invalid/)\n\n<img src=\"file://host.invalid/share/z.png\">\n\n"
                + "![referenced][r]\n\n[r]: file://host.invalid/share/r.png\n";
            var dialog = new UpdateDialog("1.3.0", "1.2.2", notes, _ => Task.CompletedTask);
            var everything = LogicalDescendants(dialog.Notes.Document).ToList();
            Assert.DoesNotContain(everything, element => element is InlineUIContainer or BlockUIContainer or Image or Button);
            var text = new TextRange(dialog.Notes.Document.ContentStart, dialog.Notes.Document.ContentEnd).Text;
            Assert.Contains("beacon", text);
            Assert.Contains("share", text);
            Assert.Contains("nested", text);
            Assert.Contains("referenced", text);
        });
    }

    private static IEnumerable<object> LogicalDescendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            yield return child;
            if (child is DependencyObject inner)
            {
                foreach (var descendant in LogicalDescendants(inner))
                {
                    yield return descendant;
                }
            }
        }
    }

    [Fact]
    public void PasswordBoxHelperSyncsBothWays()
    {
        WpfApp.Invoke(() =>
        {
            var box = new PasswordBox();
            PasswordBoxHelper.SetBoundPassword(box, "secret");
            Assert.Equal("secret", box.Password);
            box.Password = "typed";
            Assert.Equal("typed", PasswordBoxHelper.GetBoundPassword(box));
        });
    }

    /// <summary>
    /// The regression that made every secret on a NEW connector inert: the bridge used to hook
    /// PasswordChanged from the BoundPassword changed callback, which WPF never raises when the
    /// first binding transfer equals the property's default - and a new connector's token,
    /// header value and client secret all start "". The subscription is a class handler now, so
    /// the empty first transfer is irrelevant.
    /// </summary>
    [Fact]
    public void PasswordBoxPushesTypedTextWhenTheBoundValueStartedEmpty()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        WpfApp.Invoke(() =>
        {
            var model = new EditorModel(state, EditTarget.NewRemote(EditorWindow.NewRemoteStyle, "Default"), h.Dialogs, EditorWindow.NewRemoteStyle);
            Assert.Equal("", model.BearerToken);
            var box = new PasswordBox();
            BindingOperations.SetBinding(box, PasswordBoxHelper.BoundPasswordProperty,
                new Binding(nameof(EditorModel.BearerToken)) { Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

            box.Password = "secret";   // the user typing into a box whose model value was ""
            Assert.Equal("secret", model.BearerToken);

            model.BearerToken = "from the model";   // and the model → box direction still works
            Assert.Equal("from the model", box.Password);
        });
    }

    /// <summary>A PasswordBox nobody attached the bridge to keeps its text to itself.</summary>
    [Fact]
    public void PasswordBoxWithoutTheAttachedBindingIsLeftAlone()
    {
        WpfApp.Invoke(() =>
        {
            var box = new PasswordBox { Password = "untouched" };
            Assert.Equal("", PasswordBoxHelper.GetBoundPassword(box));
        });
    }

    [Fact]
    public void WpfDialogsFallsBackToTheActiveWindowWhenNoOwnerWasGiven()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var services = h.Services();
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            var dialogs = new WpfDialogs(() => null);
            Assert.Null(dialogs.ResolveOwner());   // nothing of ours is up: centred on screen, topmost

            // The flyout hides itself the moment something takes the focus, which is exactly what
            // showing a modal does - so Quit / Restart Required / the collection prompts must never
            // be owned by it, however visible and active it is when they are raised.
            using var model = new FlyoutModel(state, h.Settings);
            var flyout = new FlyoutWindow(model, new WindowRegistry(state, services, updates, h.Dialogs)) { TrayAnchor = () => null };
            flyout.Show();
            flyout.Activate();
            Assert.True(flyout.IsVisible);
            Assert.Null(dialogs.ResolveOwner());

            var window = new Window { Width = 100, Height = 100, ShowInTaskbar = false, Left = FlyoutWindow.OffScreen, Top = FlyoutWindow.OffScreen };
            window.Show();
            window.Activate();
            // Settings ▸ Check for Updates reaches the coordinator's ownerless WpfDialogs;
            // the dialog must still centre on Settings rather than on the screen.
            Assert.Same(window, dialogs.ResolveOwner());
            window.Close();
            flyout.HideFlyout();
            flyout.Close();
        });
    }
}

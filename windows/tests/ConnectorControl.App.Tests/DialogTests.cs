using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ConnectorControl.App.Tests.TestSupport;
using ConnectorControl.App.Views;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;
using AppServices = ConnectorControl.App.Services.Services;

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
    public void ConfirmDialogWithoutInformativeTextOrCancelHidesThem()
    {
        WpfApp.Invoke(() =>
        {
            var dialog = new ConfirmDialog("You're up to date.", null, "OK", null, destructive: false);
            Assert.Equal(Visibility.Collapsed, dialog.InformativeText.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.CancelButton.Visibility);
            var destructive = new ConfirmDialog("Delete Profile “Work”?", "Its connector list is removed; backups keep prior states.", "Delete", "Cancel", destructive: true);
            Assert.Same(destructive.TryFindResource("DestructiveButton"), destructive.PrimaryButton.Style);
        });
    }

    [Fact]
    public void NamePromptDialogPrefillsAndSelectsTheText()
    {
        WpfApp.Invoke(() =>
        {
            var dialog = new NamePromptDialog("Rename Profile", "Default");
            Assert.Equal("Rename Profile", dialog.Title);
            Assert.Equal("Default", dialog.NameBox.Text);
            Assert.Null(dialog.Result);
        });
    }

    [Fact]
    public void UpdateDialogRendersTheReleaseNotes()
    {
        WpfApp.Invoke(() =>
        {
            var dialog = new UpdateDialog("1.3.0", "1.2.2", "## Fixes\n- one\n- two");
            Assert.Equal(UpdateCoordinator.AvailableHeadline, dialog.Headline.Text);
            Assert.Equal("Connector Control 1.3.0 is now available — you have 1.2.2. Would you like to install it now?", dialog.Detail.Text);
            Assert.NotNull(dialog.Notes.Document);
            Assert.True(dialog.Notes.Document.Blocks.Count >= 2);   // a heading and a list
            Assert.Equal("Install and Relaunch", dialog.InstallButton.Content);
            Assert.Equal("Later", dialog.LaterButton.Content);
        });
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
    /// first binding transfer equals the property's default — and a new connector's token,
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
            var model = new EditorModel(state, EditTarget.NewRemote(EditorWindow.NewRemoteStyle), h.Dialogs, EditorWindow.NewRemoteStyle);
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
    public void WpfDialogsMatchesTheSeamTheFakeMirrors()
    {
        // The fake in Core.Tests supplies the same defaults; if these drift apart, a call
        // site compiles against one shape and is tested against another.
        var confirm = typeof(WpfDialogs).GetMethod(nameof(IDialogs.Confirm))!;
        Assert.Equal("Cancel", confirm.GetParameters()[3].DefaultValue);
        Assert.Equal(false, confirm.GetParameters()[4].DefaultValue);
        Assert.True(typeof(WpfDialogs).IsSealed);
        Assert.True(typeof(IDialogs).IsAssignableFrom(typeof(WpfDialogs)));
    }

    [Fact]
    public void WpfDialogsFallsBackToTheActiveWindowWhenNoOwnerWasGiven()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var services = new AppServices(h.Settings, new FakeClaudeInstall(), h.Claude, h.Notifier, new FakeAutostart(), new FakeUpdater());
        using var updates = new UpdateCoordinator(services.Updater, h.Settings, h.Notifier, h.Dialogs, AppHost.Inline());
        WpfApp.Invoke(() =>
        {
            var dialogs = new WpfDialogs(() => null);
            Assert.Null(dialogs.ResolveOwner());   // nothing of ours is up: centred on screen, topmost

            // The flyout hides itself the moment something takes the focus, which is exactly what
            // showing a modal does — so Quit / Restart Required / the profile prompts must never
            // be owned by it, however visible and active it is when they are raised.
            using var model = new FlyoutModel(state);
            var flyout = new FlyoutWindow(model, new WindowRegistry(state, services, updates)) { TrayAnchor = () => null };
            flyout.Show();
            flyout.Activate();
            Assert.True(flyout.IsVisible);
            Assert.Null(dialogs.ResolveOwner());

            var window = new Window { Width = 100, Height = 100, ShowInTaskbar = false, Left = -10000, Top = -10000 };
            window.Show();
            window.Activate();
            // Settings ▸ Check for Updates… reaches the coordinator's ownerless WpfDialogs;
            // the dialog must still centre on Settings rather than on the screen.
            Assert.Same(window, dialogs.ResolveOwner());
            window.Close();
            flyout.HideFlyout();
            flyout.Close();
        });
    }
}

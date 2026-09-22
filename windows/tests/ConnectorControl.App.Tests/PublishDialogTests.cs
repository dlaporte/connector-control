using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ConnectorControl.App.Tests.TestSupport;
using ConnectorControl.App.Views;
using ConnectorControl.Core;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.App.Tests;

/// <summary>
/// The Publish sheet and its Export mode: which fields a tick shows or hides, that the preview
/// under them is the document as the ticks now stand, and that nothing publishes until a folder
/// is chosen. The rules are PublishModel's and tested there; these are the bindings.
/// </summary>
public class PublishDialogTests
{
    private static void Layout(Window window) => WindowTestSupport.Layout(window, new Size(620, 620));

    /// <summary>
    /// One local connector with a stripped environment value that looks like a credential, a
    /// harmless one, and one absolute path argument — a row of each kind the sheet offers.
    /// </summary>
    private static JsonValue Connector() => JsonValue.Object(
        ("command", JsonValue.String("node")),
        ("args", JsonValue.Array([JsonValue.String("/Users/d/x.js"), JsonValue.String("--quiet")])),
        ("env", JsonValue.Object(("A", JsonValue.String("sk-live-secret")), ("B", JsonValue.String("us")))));

    private static AppState Started(AppStateHarness h)
    {
        var state = h.Create();
        Assert.Null(state.Upsert("c", new McpEntry(true, Connector()), null));
        return state;
    }

    /// <summary>
    /// A named element inside one row's template instance. The rows are an ItemsControl's, so the
    /// container is the ContentPresenter the item template was applied to.
    /// </summary>
    private static T Named<T>(ItemsControl list, object row, string name)
        where T : FrameworkElement
    {
        var presenter = (ContentPresenter)list.ItemContainerGenerator.ContainerFromItem(row)!;
        presenter.ApplyTemplate();
        return (T)presenter.ContentTemplate.FindName(name, presenter);
    }

    /// <summary>
    /// A click, in the two halves WPF splits it into: the state the tick lands in, and the Click
    /// the sheet listens to. Setting IsChecked on its own leaves the preview below stale, which is
    /// the one thing about this sheet a test must not hide.
    /// </summary>
    private static void ClickTick(CheckBox tick, bool on)
    {
        tick.IsChecked = on;
        tick.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, tick));
    }

    /// <summary>A row list generates no containers until the window has had a real layout pass.</summary>
    private static PublishDialog Shown(PublishModel model)
    {
        var window = new PublishDialog(model, PublishDialogMode.Publish);
        window.Show();
        Layout(window);
        window.EnvList.UpdateLayout();
        window.PathList.UpdateLayout();
        return window;
    }

    [Fact]
    public void SharingAValueRemovesItsHintField()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        WpfApp.Invoke(() =>
        {
            var model = new PublishModel(state, state.ActiveCollection);
            var window = Shown(model);
            var row = model.EnvRows.Single(r => r.Name == "A");
            var tick = Named<CheckBox>(window.EnvList, row, "ShareTick");
            var hint = Named<TextBox>(window.EnvList, row, "HintBox");

            Assert.Equal(PublishModel.ShareValueLabel, tick.Content);
            Assert.False(tick.IsChecked);
            Assert.Equal(PublishModel.HintPlaceholder, hint.Tag);
            Assert.Equal(Visibility.Visible, hint.Visibility);

            hint.Text = "the ledger dashboard ▸ API tokens";
            Assert.Equal("the ledger dashboard ▸ API tokens", row.Hint);
            // A stripped value travels as its name and this hint, so the hint is in the document.
            Assert.Contains("the ledger dashboard ▸ API tokens", window.PreviewBox.Text, StringComparison.Ordinal);

            ClickTick(tick, true);
            Layout(window);

            // A shared value needs no hint: its own value is what travels.
            Assert.Equal(Visibility.Collapsed, hint.Visibility);
            Assert.True(row.Share);
            Assert.Equal("A", Assert.Single(model.Intent.ShareValues["c"]));
            window.Close();
        });
    }

    [Fact]
    public void MarkingAPathRevealsNameAndHint()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        WpfApp.Invoke(() =>
        {
            var model = new PublishModel(state, state.ActiveCollection);
            var window = Shown(model);
            var row = Assert.Single(model.PathRows);
            var tick = Named<CheckBox>(window.PathList, row, "MarkTick");
            var fields = Named<StackPanel>(window.PathList, row, "MarkedFields");
            var nameBox = Named<TextBox>(window.PathList, row, "PathNameBox");
            var hintBox = Named<TextBox>(window.PathList, row, "PathHintBox");

            Assert.False(tick.IsChecked);
            Assert.Equal(Visibility.Collapsed, fields.Visibility);
            Assert.Equal(PublishModel.PathNamePlaceholder, nameBox.Tag);
            Assert.Equal(PublishModel.HintPlaceholder, hintBox.Tag);
            // Unmarked, the path on this machine is what the document carries.
            Assert.Contains("/Users/d/x.js", window.PreviewBox.Text, StringComparison.Ordinal);

            ClickTick(tick, true);
            Layout(window);

            Assert.Equal(Visibility.Visible, fields.Visibility);
            Assert.Equal("path", nameBox.Text);
            nameBox.Text = "server_path";
            hintBox.Text = "your ledger clone, then dist/index.js";
            Layout(window);

            Assert.Equal(new PublishIntent.PathMark("server_path", "your ledger clone, then dist/index.js"),
                model.Intent.PathMarks["c"][JsonPointer.Parse("/args/0")!]);
            Assert.Contains("${CC_NEEDS:server_path}", window.PreviewBox.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("/Users/d/x.js", window.PreviewBox.Text, StringComparison.Ordinal);
            window.Close();
        });
    }

    [Fact]
    public void ThePreviewHidesUnsharedValues()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        WpfApp.Invoke(() =>
        {
            var model = new PublishModel(state, state.ActiveCollection);
            var window = Shown(model);
            var row = model.EnvRows.Single(r => r.Name == "A");

            // Stripped is the default, and a value that never leaves is nothing to warn about.
            Assert.DoesNotContain("sk-live-secret", window.PreviewBox.Text, StringComparison.Ordinal);
            Assert.True(window.PreviewBox.IsReadOnly);
            Assert.Empty(window.WarningList.Items);

            ClickTick(Named<CheckBox>(window.EnvList, row, "ShareTick"), true);
            Layout(window);

            // The preview shows every byte that leaves, and says which of them look like secrets.
            Assert.Contains("sk-live-secret", window.PreviewBox.Text, StringComparison.Ordinal);
            Assert.Equal(PublishModel.WarningLine("c", "env.A looks like a credential"),
                Assert.Single(window.WarningList.Items));

            ClickTick(Named<CheckBox>(window.EnvList, row, "ShareTick"), false);
            Layout(window);

            Assert.DoesNotContain("sk-live-secret", window.PreviewBox.Text, StringComparison.Ordinal);
            Assert.Empty(window.WarningList.Items);
            window.Close();
        });
    }

    [Fact]
    public void PublishIsDisabledUntilAFolderIsChosen()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        var folder = h.Dir.File("pub");
        Directory.CreateDirectory(folder);
        WpfApp.Invoke(() =>
        {
            var model = new PublishModel(state, state.ActiveCollection);
            var window = new PublishDialog(model, PublishDialogMode.Publish);
            Layout(window);

            Assert.Equal(model.SheetTitle, window.Title);
            Assert.Equal(Visibility.Visible, window.FolderRow.Visibility);
            Assert.Equal(Visibility.Visible, window.PublishButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.ExportButton.Visibility);
            Assert.True(string.IsNullOrEmpty(window.FolderText.Text));
            Assert.False(window.PublishButton.IsEnabled);

            // All the folder picker does is hand the model one path.
            model.Folder = folder;
            Layout(window);
            Assert.Equal(folder, window.FolderText.Text);
            Assert.True(window.PublishButton.IsEnabled);

            // Export asks the save panel for a path when its button is pressed, so it has no
            // folder row and nothing to wait for.
            var export = new PublishDialog(new PublishModel(state, state.ActiveCollection), PublishDialogMode.Export);
            Layout(export);
            Assert.Equal(FlyoutModel.ExportTitleFor(state.ActiveCollection), export.Title);
            Assert.Equal(Visibility.Collapsed, export.FolderRow.Visibility);
            Assert.Equal(Visibility.Collapsed, export.PublishButton.Visibility);
            Assert.Equal(Visibility.Visible, export.ExportButton.Visibility);
            Assert.Equal(PublishModel.ExportButton, export.ExportButton.Content);
            Assert.True(export.ExportButton.IsEnabled);
        });
    }
}

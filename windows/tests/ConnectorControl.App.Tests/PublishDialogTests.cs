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
    /// A tick, moved through the control the way a click moves it. Nothing else is needed: the
    /// two-way binding writes the row, and the row's own notification is what reaches the preview.
    /// </summary>
    private static void ClickTick(CheckBox tick, bool on) => tick.IsChecked = on;

    /// <summary>A row list generates no containers until the window has had a real layout pass.</summary>
    private static PublishDialog Shown(PublishModel model, PublishDialogMode mode = PublishDialogMode.Publish)
    {
        var window = new PublishDialog(model, mode);
        window.Show();
        Layout(window);
        window.EnvList.UpdateLayout();
        window.PathList.UpdateLayout();
        window.UnresolvedList.UpdateLayout();
        window.KeptList.UpdateLayout();
        return window;
    }

    /// <summary>A button press, raised the way WPF raises it: synchronously, on the button itself.</summary>
    private static void Press(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    /// <summary>
    /// "c" published with its path marked "srv", then that path edited outside the sheet, so the
    /// record's mark has lost the argument it hid. The folder stays on record.
    /// </summary>
    private static void PublishThenMoveTheMarkedPath(AppStateHarness h, AppState state)
    {
        var folder = h.Dir.File("pub");
        Directory.CreateDirectory(folder);
        var first = new PublishModel(state, state.ActiveCollection) { Folder = folder };
        first.PathRows.Single(r => r.Connector == "c").Marked = true;
        first.PathRows.Single(r => r.Connector == "c").Name = "srv";
        Assert.Null(first.Publish());
        var moved = JsonValue.Object(
            ("command", JsonValue.String("node")),
            ("args", JsonValue.Array([JsonValue.String("/Users/d/y.js"), JsonValue.String("--quiet")])));
        Assert.Null(state.Upsert("c", new McpEntry(true, moved), "c"));
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
            var tick = RowElements.Find<CheckBox>(window.EnvList, row, "ShareTick");
            var hint = RowElements.Find<TextBox>(window.EnvList, row, "HintBox");

            Assert.Equal(PublishModel.ShareValueLabel, tick.Content);
            Assert.False(tick.IsChecked);
            Assert.Equal(PublishModel.HintPlaceholder, hint.Tag);
            Assert.Equal(Visibility.Visible, hint.Visibility);
            // A stripped value is not in the document and not on the row: the hint field is what
            // this row is asking for.
            var value = RowElements.Find<TextBlock>(window.EnvList, row, "ValueText");
            Assert.Equal(Visibility.Collapsed, value.Visibility);

            hint.Text = "the ledger dashboard ▸ API tokens";
            Assert.Equal("the ledger dashboard ▸ API tokens", row.Hint);
            // A stripped value travels as its name and this hint, so the hint is in the document.
            Assert.Contains("the ledger dashboard ▸ API tokens", window.PreviewBox.Text, StringComparison.Ordinal);

            ClickTick(tick, true);
            Layout(window);

            // A shared value needs no hint: its own value is what travels, so the row shows it and
            // drops the hint field.
            Assert.Equal(Visibility.Collapsed, hint.Visibility);
            Assert.Equal(Visibility.Visible, value.Visibility);
            Assert.Equal("sk-live-secret", value.Text);
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
            var tick = RowElements.Find<CheckBox>(window.PathList, row, "MarkTick");
            var fields = RowElements.Find<StackPanel>(window.PathList, row, "MarkedFields");
            var nameBox = RowElements.Find<TextBox>(window.PathList, row, "PathNameBox");
            var hintBox = RowElements.Find<TextBox>(window.PathList, row, "PathHintBox");

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

            Assert.Equal(new PublishIntent.PathMark("server_path", "your ledger clone, then dist/index.js", "/Users/d/x.js"),
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

            ClickTick(RowElements.Find<CheckBox>(window.EnvList, row, "ShareTick"), true);
            Layout(window);

            // The preview shows every byte that leaves, and says which of them look like secrets.
            Assert.Contains("sk-live-secret", window.PreviewBox.Text, StringComparison.Ordinal);
            Assert.Equal(PublishModel.WarningLine("c", "env.A looks like a credential"),
                Assert.Single(window.WarningList.Items));

            ClickTick(RowElements.Find<CheckBox>(window.EnvList, row, "ShareTick"), false);
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
            Assert.Equal(model.SheetTitle, window.TitleText.Text);
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
            Assert.Equal(PublishModel.ExportTitle(state.ActiveCollection), export.Title);
            Assert.Equal(export.Title, export.TitleText.Text);
            Assert.Equal(Visibility.Collapsed, export.FolderRow.Visibility);
            Assert.Equal(Visibility.Collapsed, export.PublishButton.Visibility);
            Assert.Equal(Visibility.Visible, export.ExportButton.Visibility);
            Assert.Equal(PublishModel.ExportButton, export.ExportButton.Content);
            // Nothing holds an export here: no mark is unresolved.
            Assert.True(export.ExportButton.IsEnabled);
            Assert.Empty(export.UnresolvedList.Items);
            Assert.False(export.Accepted);
        });
    }

    [Fact]
    public void AMovedMarkHoldsPublishAndExportUntilItIsForgotten()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        PublishThenMoveTheMarkedPath(h, state);
        WpfApp.Invoke(() =>
        {
            var model = new PublishModel(state, state.ActiveCollection);
            var window = Shown(model);

            // A folder is on record, and still nothing can be published: the path the mark hid
            // would travel as written. The sheet says why, beside the way out.
            Assert.False(string.IsNullOrEmpty(model.Folder));
            Assert.False(window.PublishButton.IsEnabled);
            var lost = Assert.Single(model.UnresolvedMarks);
            var note = RowElements.Find<TextBlock>(window.UnresolvedList, lost, "MarkNoteText");
            Assert.Equal(PublishModel.UnresolvedMarkNote("c", "srv"), note.Text);
            var forget = RowElements.Find<Button>(window.UnresolvedList, lost, "ForgetMark");
            Assert.Equal(PublishModel.ForgetMarkButton, forget.Content);

            Press(forget);
            Layout(window);

            Assert.Empty(window.UnresolvedList.Items);
            Assert.True(window.PublishButton.IsEnabled);
            window.Close();

            // Forgetting belongs to the sheet it was pressed in: a fresh one on the same record
            // holds Export the same way, until its own Forget Mark.
            var fresh = new PublishModel(state, state.ActiveCollection);
            var export = Shown(fresh, PublishDialogMode.Export);
            Assert.False(export.ExportButton.IsEnabled);

            Press(RowElements.Find<Button>(export.UnresolvedList, Assert.Single(fresh.UnresolvedMarks), "ForgetMark"));
            Layout(export);

            Assert.Empty(export.UnresolvedList.Items);
            Assert.True(export.ExportButton.IsEnabled);
            export.Close();
        });
    }

    private const string LedgerPath = "/Users/d/ledger/dist/index.js";

    [Fact]
    public void ALostMarkAndAKeptPathEachHoldTheSheetUntilAnswered()
    {
        using var h = new AppStateHarness();
        using var state = Started(h);
        PublishThenMoveTheMarkedPath(h, state);
        Assert.Null(state.Upsert("ledger", new McpEntry(true, JsonValue.Object(
            ("command", JsonValue.String("node")),
            ("args", JsonValue.Array([JsonValue.String(LedgerPath)])))), null));
        WpfApp.Invoke(() =>
        {
            var model = new PublishModel(state, state.ActiveCollection);
            var window = Shown(model);

            // A kept path: ledger's path ticked, then written out whole in its own hint, which
            // would carry it into the document as written.
            var ledger = model.PathRows.Single(r => r.Connector == "ledger");
            RowElements.Find<CheckBox>(window.PathList, ledger, "MarkTick").IsChecked = true;
            Layout(window);
            RowElements.Find<TextBox>(window.PathList, ledger, "PathHintBox").Text = $"like mine, {LedgerPath}";
            Layout(window);

            // Both entries are listed, each with its own note and its own way out.
            var lost = Assert.Single(model.UnresolvedMarks);
            var kept = Assert.Single(model.KeptPaths);
            Assert.Equal(PublishModel.UnresolvedMarkNote("c", "srv"),
                RowElements.Find<TextBlock>(window.UnresolvedList, lost, "MarkNoteText").Text);
            Assert.Equal(PublishModel.KeptPathNote("ledger", kept.Field),
                RowElements.Find<TextBlock>(window.KeptList, kept, "KeptNoteText").Text);
            Assert.Equal(PublishModel.ForgetMarkButton, RowElements.Find<Button>(window.UnresolvedList, lost, "ForgetMark").Content);
            Assert.Equal(PublishModel.ReleaseValueButton, RowElements.Find<Button>(window.KeptList, kept, "ReleaseValue").Content);
            // A kept path, not a folder of the collection's own: Release is its answer, and the token is not offered.
            Assert.Equal(PublishModel.KeptPathKind.Path, kept.Kind);
            Assert.Equal(Visibility.Visible, RowElements.Find<Button>(window.KeptList, kept, "ReleaseValue").Visibility);
            Assert.Equal(Visibility.Collapsed, RowElements.Find<Button>(window.KeptList, kept, "UseDirectoryToken").Visibility);
            // The export button is bound to its gate in both modes, so one sheet shows both.
            Assert.False(window.PublishButton.IsEnabled);
            Assert.False(window.ExportButton.IsEnabled);

            // Each entry is answered alone: forgetting the mark leaves the kept path holding both.
            Press(RowElements.Find<Button>(window.UnresolvedList, lost, "ForgetMark"));
            Layout(window);
            Assert.Empty(window.UnresolvedList.Items);
            Assert.Single(window.KeptList.Items);
            Assert.False(window.PublishButton.IsEnabled);
            Assert.False(window.ExportButton.IsEnabled);

            // The list was re-read when the mark was forgotten, so its line is found again.
            Press(RowElements.Find<Button>(window.KeptList, kept, "ReleaseValue"));
            Layout(window);
            Assert.Empty(window.KeptList.Items);
            // Released, the path travels as written, so no row goes on marking it.
            Assert.False(RowElements.Find<CheckBox>(window.PathList, ledger, "MarkTick").IsChecked);
            Assert.True(window.PublishButton.IsEnabled);
            Assert.True(window.ExportButton.IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void AFolderInTheCommandIsAnsweredByTheDirectoryTokenAlone()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = h.Dir.File("pub");
        Directory.CreateDirectory(folder);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        var bound = state.CollectionsCache.Published[state.ActiveCollection].Folder;
        // The collection's own publish folder, written out where no argument row reaches it.
        Assert.Null(state.Upsert("tool", new McpEntry(true, JsonValue.Object(
            ("command", JsonValue.String(bound + "/bin/tool")))), null));
        WpfApp.Invoke(() =>
        {
            var model = new PublishModel(state, state.ActiveCollection);
            var window = Shown(model);

            var kept = Assert.Single(model.KeptPaths);
            Assert.Equal(("tool", "local.command", PublishModel.KeptPathKind.Folder), (kept.Connector, kept.Field, kept.Kind));
            Assert.Equal(PublishModel.PublishFolderNote("tool", "local.command"),
                RowElements.Find<TextBlock>(window.KeptList, kept, "KeptNoteText").Text);
            // One answer only: released, the folder would travel as written.
            var use = RowElements.Find<Button>(window.KeptList, kept, "UseDirectoryToken");
            Assert.Equal(PublishModel.UseDirectoryTokenButton, use.Content);
            Assert.Equal(Visibility.Visible, use.Visibility);
            Assert.Equal(Visibility.Collapsed, RowElements.Find<Button>(window.KeptList, kept, "ReleaseValue").Visibility);
            // A folder is on record, and the entry still holds Publish.
            Assert.False(string.IsNullOrEmpty(model.Folder));
            Assert.False(window.PublishButton.IsEnabled);

            Press(use);
            Layout(window);

            // The token is written in the connector itself, where the folder sat.
            Assert.Equal(JsonValue.Object(("command", JsonValue.String($"{Placeholder.DirectoryToken}/bin/tool"))),
                state.Store.Collections[state.ActiveCollection].Mcps["tool"].Config);
            Assert.Empty(window.KeptList.Items);
            Assert.Equal(Visibility.Collapsed, window.FailureText.Visibility);
            Assert.True(window.PublishButton.IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void AFolderTheSheetCannotRewriteSaysWhereToWriteTheToken()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var folder = h.Dir.File("pub");
        Directory.CreateDirectory(folder);
        Assert.Null(state.StartPublishing(state.ActiveCollection, folder, PublishIntent.None));
        var bound = state.CollectionsCache.Published[state.ActiveCollection].Folder;
        // The folder as a remote connector's header name, which the sheet's rewrite cannot reach.
        Assert.Null(state.Upsert("svc", new McpEntry(RemotePattern.Encode(new RemoteConfig(
            "https://mcp.example.com/", new RemoteAuth.Header(bound, "v"), RemoteLaunchStyle.Npx, package: "mcp-remote"))), null));
        WpfApp.Invoke(() =>
        {
            var model = new PublishModel(state, state.ActiveCollection);
            var window = Shown(model);

            // A folder of this collection's own wherever it sits, so the token is still its answer,
            // but the note says the connector's editor is where to write it.
            var kept = Assert.Single(model.KeptPaths);
            Assert.Equal(("svc", "remote.auth.name", PublishModel.KeptPathKind.Folder), (kept.Connector, kept.Field, kept.Kind));
            var note = PublishModel.PublishFolderEditNote("svc", "remote.auth.name");
            Assert.Equal(note, RowElements.Find<TextBlock>(window.KeptList, kept, "KeptNoteText").Text);
            var use = RowElements.Find<Button>(window.KeptList, kept, "UseDirectoryToken");
            Assert.Equal(Visibility.Visible, use.Visibility);
            Assert.Equal(Visibility.Collapsed, RowElements.Find<Button>(window.KeptList, kept, "ReleaseValue").Visibility);
            Assert.False(window.PublishButton.IsEnabled);

            Press(use);
            Layout(window);

            // Nothing was written: the entry stays, and the sheet says what does answer it.
            Assert.Single(window.KeptList.Items);
            Assert.Equal(Visibility.Visible, window.FailureText.Visibility);
            Assert.Equal(note, window.FailureText.Text);
            Assert.False(window.PublishButton.IsEnabled);
            window.Close();
        });
    }
}

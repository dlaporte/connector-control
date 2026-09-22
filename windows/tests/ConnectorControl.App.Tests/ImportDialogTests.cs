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
/// The Import sheet: that the two modes are exclusive and the count beside Import follows the
/// ticks, that a document this app cannot read leaves nothing but Cancel, and that a connector
/// this platform cannot run says why and cannot be ticked. The rules are ImportModel's and tested
/// there; these are the bindings.
/// </summary>
public class ImportDialogTests
{
    private static void Layout(Window window) => WindowTestSupport.Layout(window, new Size(620, 620));

    private static void Write(CollectionDocument document, string path) =>
        File.WriteAllBytes(path, document.Serialize());

    /// <summary>A row list generates no containers until the window has had a real layout pass.</summary>
    private static ImportDialog Shown(ImportModel model)
    {
        var window = new ImportDialog(model);
        window.Show();
        Layout(window);
        window.RowList.UpdateLayout();
        return window;
    }

    /// <summary>
    /// A click, in the two halves WPF splits it into: the state the tick lands in, and the Click
    /// the sheet listens to. Setting IsChecked on its own leaves the count beside Import stale,
    /// which is the one thing about this sheet a test must not hide.
    /// </summary>
    private static void ClickTick(ToggleButton tick, bool on)
    {
        tick.IsChecked = on;
        tick.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, tick));
    }

    /// <summary>
    /// Two remote connectors, one with a header name carrying the &amp; the Windows cmd /c
    /// launcher cannot hand to cmd.exe.
    /// </summary>
    private static CollectionDocument RiskyHeaderDocument() => new(
        "Risky", "Acme", "o-risky", "2026-09-21T14:02:11Z",
        new Dictionary<string, CollectionDocument.Connector>
        {
            ["bad"] = new(new CollectionDocument.Launcher.Remote(
                "https://h/mcp", new CollectionDocument.Auth.Header("X&Y"), "mcp-remote", [])),
            ["good"] = new(new CollectionDocument.Launcher.Remote(
                "https://h/mcp", CollectionDocument.Auth.Auto, "mcp-remote", [])),
        });

    [Fact]
    public void TwoModesAreExclusiveAndTheCountFollows()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("data-team.json");
        Write(CollectionDocumentSamples.DataTeam, path);
        WpfApp.Invoke(() =>
        {
            var model = new ImportModel(state, path);
            var window = Shown(model);

            Assert.Equal(ImportModel.Title, window.Title);
            // The mode's own sentence names the collection the copies would land in.
            Assert.Equal(ImportModel.AddModeTitle("Default"), window.CopiesMode.Content);
            Assert.True(window.CopiesMode.IsChecked);
            Assert.False(window.SyncMode.IsChecked);
            Assert.Equal(Visibility.Visible, window.CopiesBody.Visibility);
            Assert.Equal(Visibility.Collapsed, window.SyncBody.Visibility);
            Assert.Equal("Default", window.TargetBox.SelectedItem);
            // Nothing in this collection answers to any of the four names, so all four are ticked.
            Assert.Equal(ImportModel.ImportButton(4), window.ImportButton.Content);
            Assert.True(window.ImportButton.IsEnabled);

            // A connector whose author left values to fill in carries the caution its own row in
            // the collection would; one with nothing to fill in carries no glyph at all.
            var dbt = model.Rows.Single(r => r.Name == "dbt");
            var glyph = RowElements.Find<TextBlock>(window.RowList, dbt, "NeedsGlyph");
            Assert.Equal(dbt.NeedsCaution, glyph.ToolTip);
            Assert.Equal(Visibility.Visible, glyph.Visibility);
            Assert.Equal(Visibility.Collapsed,
                RowElements.Find<TextBlock>(window.RowList, model.Rows.Single(r => r.Name == "github"), "NeedsGlyph").Visibility);

            // Unticking a row takes it out of the count.
            ClickTick(RowElements.Find<CheckBox>(window.RowList, dbt, "IncludeTick"), false);
            Layout(window);
            Assert.False(dbt.Include);
            Assert.Equal(ImportModel.ImportButton(3), window.ImportButton.Content);

            // The other mode takes the whole document, so the ticks stop counting.
            ClickTick(window.SyncMode, true);
            Layout(window);
            Assert.False(window.CopiesMode.IsChecked);
            Assert.Equal(ImportModel.Mode.KeepInSync, model.ImportMode);
            Assert.Equal(Visibility.Collapsed, window.CopiesBody.Visibility);
            Assert.Equal(Visibility.Visible, window.SyncBody.Visibility);
            Assert.Equal("Data team", window.SyncNameBox.Text);
            Assert.Equal(ImportModel.ImportButton(4), window.ImportButton.Content);

            // And back again: one mode at a time, the whole way, and the ticks were kept.
            ClickTick(window.CopiesMode, true);
            Layout(window);
            Assert.False(window.SyncMode.IsChecked);
            Assert.Equal(ImportModel.Mode.AddToCollection, model.ImportMode);
            Assert.Equal(ImportModel.ImportButton(3), window.ImportButton.Content);

            // A collection has to be called something, and the button follows the field.
            ClickTick(window.SyncMode, true);
            window.SyncNameBox.Text = "  ";
            Layout(window);
            Assert.Equal("  ", model.SyncName);
            Assert.False(window.ImportButton.IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void ANewerDocumentShowsTheLoadError()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var newer = CollectionDocumentSamples.DataTeam.Encode()
            .Replacing(JsonPointer.Parse("/connectorControlCollection")!, JsonValue.Int(2))!;
        var path = h.Dir.File("future.json");
        File.WriteAllBytes(path, newer.Serialize());
        WpfApp.Invoke(() =>
        {
            var model = new ImportModel(state, path);
            var window = new ImportDialog(model);
            window.Show();
            Layout(window);

            Assert.Equal(AppState.NewerDocumentError, window.LoadErrorText.Text);
            Assert.Equal(Visibility.Visible, window.LoadErrorText.Visibility);
            // Nothing to choose between, and nothing to press but Cancel.
            Assert.Equal(Visibility.Collapsed, window.Body.Visibility);
            Assert.Equal(Visibility.Collapsed, window.ImportButton.Visibility);
            Assert.Equal(Visibility.Visible, window.CancelButton.Visibility);
            Assert.True(window.CancelButton.IsCancel);
            // The file still says which document the sheet is about.
            Assert.Contains("future.json", model.SourceSentence, StringComparison.Ordinal);
            window.Close();
        });
    }

    [Fact]
    public void ASkippedRowShowsItsReason()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = h.Dir.File("risky.json");
        Write(RiskyHeaderDocument(), path);
        WpfApp.Invoke(() =>
        {
            var model = new ImportModel(state, path);
            var window = Shown(model);
            var bad = model.Rows.Single(r => r.Name == "bad");
            var good = model.Rows.Single(r => r.Name == "good");

            var skipped = RowElements.Find<TextBlock>(window.RowList, bad, "SkippedText");
            Assert.Equal(ImportModel.SkippedBadge(bad.ExcludedReason!), skipped.Text);
            Assert.Equal(Visibility.Visible, skipped.Visibility);
            // A connector this platform has no way to run cannot be imported at all.
            var tick = RowElements.Find<CheckBox>(window.RowList, bad, "IncludeTick");
            Assert.False(tick.IsEnabled);
            Assert.False(tick.IsChecked);
            Assert.Equal(Visibility.Collapsed,
                RowElements.Find<TextBlock>(window.RowList, bad, "NewText").Visibility);

            // The one that can come across says it is new here, and is the only one in the count.
            Assert.Equal(Visibility.Collapsed,
                RowElements.Find<TextBlock>(window.RowList, good, "SkippedText").Visibility);
            var badge = RowElements.Find<TextBlock>(window.RowList, good, "NewText");
            Assert.Equal(ImportModel.NewBadge, badge.Text);
            Assert.Equal(Visibility.Visible, badge.Visibility);
            Assert.True(RowElements.Find<CheckBox>(window.RowList, good, "IncludeTick").IsEnabled);
            Assert.Equal(ImportModel.ImportButton(1), window.ImportButton.Content);
            window.Close();
        });
    }
}

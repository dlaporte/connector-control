using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using ConnectorControl.App.Tests.TestSupport;
using ConnectorControl.App.Views;
using ConnectorControl.Core;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.App.Tests;

/// <summary>
/// The Review &amp; Apply sheet: that the changes are grouped by kind with the JSON both sides of
/// each one, that Apply lands the update and closes the sheet, and that a document which changed
/// under the sheet is said so rather than applied. The rules are ReviewModel's and tested there;
/// these are the bindings.
/// </summary>
public class ReviewDialogTests
{
    private static void Layout(Window window) => WindowTestSupport.Layout(window, new Size(760, 620));

    private static void Write(CollectionDocument document, string path) =>
        File.WriteAllBytes(path, document.Serialize());

    /// <summary>
    /// The sample with <paramref name="removed"/> gone and dbt's package changed — a removal and a
    /// change, which is two of the three kinds.
    /// </summary>
    private static CollectionDocument Changed(string dbtPackage, params string[] removed)
    {
        var sample = CollectionDocumentSamples.DataTeam;
        var connectors = new Dictionary<string, CollectionDocument.Connector>(sample.Connectors, StringComparer.Ordinal);
        foreach (var name in removed)
        {
            connectors.Remove(name);
        }
        var dbt = connectors["dbt"];
        connectors["dbt"] = new CollectionDocument.Connector(
            new CollectionDocument.Launcher.Local("npx", ["-y", dbtPackage], CollectionPlatform.Mac),
            dbt.Env, dbt.Needs, dbt.Additional);
        return new CollectionDocument(sample.Name, sample.Author, sample.Origin, sample.Exported, connectors);
    }

    /// <summary>
    /// Subscribes to the sample, then writes <paramref name="next"/> over it and reads that back
    /// through Refresh — the same read the watcher would drive, without waiting for one.
    /// </summary>
    private static string Pending(AppStateHarness h, AppState state, CollectionDocument next)
    {
        var path = h.Dir.File("data-team.json");
        Write(CollectionDocumentSamples.DataTeam, path);
        Assert.Null(state.Subscribe(path, null));
        Write(next, path);
        state.RefreshSource("Data team");
        Assert.True(state.PendingUpdates.ContainsKey("Data team"));
        return path;
    }

    private static void Click(ButtonBase button) =>
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    private static IList<object> Groups(ReviewDialog window) =>
        ((ICollectionView)window.ChangeList.ItemsSource).Groups!;

    private static IEnumerable<string> Names(object group) =>
        ((CollectionViewGroup)group).Items.Cast<ReviewModel.Row>().Select(row => row.Name);

    /// <summary>
    /// A named element in the row template instance for <paramref name="row"/>. The changes are
    /// grouped, so a row's container belongs to its group's own generator rather than the list's;
    /// the walk finds the element by the row it carries as its data instead.
    /// </summary>
    private static T Named<T>(ReviewDialog window, ReviewModel.Row row, string name)
        where T : FrameworkElement
    {
        var found = Search<T>(window, row, name);
        Assert.NotNull(found);
        return found;
    }

    private static T? Search<T>(DependencyObject root, ReviewModel.Row row, string name)
        where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name && ReferenceEquals(match.DataContext, row))
            {
                return match;
            }
            if (Search<T>(child, row, name) is { } deeper)
            {
                return deeper;
            }
        }
        return null;
    }

    [Fact]
    public void RowsAreGroupedAndApplyCloses()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Pending(h, state, Changed("@dbt/mcp@2", "github"));
        WpfApp.Invoke(() =>
        {
            var model = new ReviewModel(state, "Data team");
            var window = new ReviewDialog(model);
            window.Show();
            Layout(window);
            window.ChangeList.UpdateLayout();

            Assert.Equal(model.SheetTitle, window.Title);
            Assert.Equal(model.Summary, window.SummaryText.Text);

            // Removed, then changed: the order the rows arrive in, which is the order the summary
            // sentence reads in.
            var groups = Groups(window);
            Assert.Equal(2, groups.Count);
            Assert.Equal([ReviewModel.Kind.Removed, ReviewModel.Kind.Changed],
                groups.Cast<CollectionViewGroup>().Select(group => group.Name));
            Assert.Equal(["github"], Names(groups[0]));
            Assert.Equal(["dbt"], Names(groups[1]));

            // A removal has a before side and nothing after it; the column is still there.
            var github = model.Rows.Single(row => row.Name == "github");
            var before = Named<TextBox>(window, github, "BeforeBox");
            Assert.Equal(github.Before, before.Text);
            Assert.True(before.IsReadOnly);
            Assert.Equal(string.Empty, Named<TextBox>(window, github, "AfterBox").Text);

            // A change has both, and they are not the same document.
            var dbt = model.Rows.Single(row => row.Name == "dbt");
            Assert.Equal(dbt.Before, Named<TextBox>(window, dbt, "BeforeBox").Text);
            Assert.Contains("@dbt/mcp@2", Named<TextBox>(window, dbt, "AfterBox").Text, StringComparison.Ordinal);
            Assert.DoesNotContain("@dbt/mcp@2", Named<TextBox>(window, dbt, "BeforeBox").Text, StringComparison.Ordinal);
            Assert.Equal(Visibility.Collapsed, window.MovedPanel.Visibility);

            Click(window.ApplyButton);
            Assert.True(window.Applied);
            Assert.False(window.IsVisible);
            Assert.Empty(state.PendingUpdates);
            Assert.False(state.Store.Collections["Data team"].Mcps.ContainsKey("github"));
        });
    }

    [Fact]
    public void ASourceThatMovedShowsTheMessageAndRefreshClearsIt()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var path = Pending(h, state, Changed("@dbt/mcp@2", "github"));
        WpfApp.Invoke(() =>
        {
            var model = new ReviewModel(state, "Data team");
            var window = new ReviewDialog(model);
            window.Show();
            Layout(window);
            Assert.Equal(Visibility.Collapsed, window.MovedPanel.Visibility);

            // The author commits again while the sheet is open.
            Write(Changed("@dbt/mcp@3", "github", "notion"), path);
            state.RefreshSource("Data team");

            Click(window.ApplyButton);
            Layout(window);

            // What is listed is no longer what would land, so nothing landed.
            Assert.False(window.Applied);
            Assert.True(window.IsVisible);
            Assert.Equal(Visibility.Visible, window.MovedPanel.Visibility);
            Assert.Equal(ReviewModel.SourceMovedMessage, window.MovedText.Text);
            Assert.True(state.PendingUpdates.ContainsKey("Data team"));
            Assert.True(state.Store.Collections["Data team"].Mcps.ContainsKey("notion"));

            Click(window.RefreshButton);
            Layout(window);
            window.ChangeList.UpdateLayout();

            Assert.Equal(Visibility.Collapsed, window.MovedPanel.Visibility);
            Assert.Equal(["github", "notion", "dbt"], model.Rows.Select(row => row.Name));
            var groups = Groups(window);
            Assert.Equal(["github", "notion"], Names(groups[0]));
            Assert.Equal(["dbt"], Names(groups[1]));

            Click(window.ApplyButton);
            Assert.True(window.Applied);
            Assert.False(window.IsVisible);
            Assert.Empty(state.PendingUpdates);
        });
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ConnectorControl.App.Tests.TestSupport;
using ConnectorControl.App.Views;
using ConnectorControl.Core;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.App.Tests;

public class EditorWindowTests
{
    private static void Layout(Window window)
    {
        // WPF defers a binding's first target update to DataBind priority; the test host runs
        // the body synchronously, so pump that queue before reading any bound state.
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        window.Measure(new Size(540, 620));
        window.Arrange(new Rect(0, 0, 540, 620));
        window.UpdateLayout();
    }

    [Fact]
    public void NewRemoteTargetShowsTheRemoteFormWithTheTypePicker()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        WpfApp.Invoke(() =>
        {
            var window = new EditorWindow(state, EditTarget.NewRemote(EditorWindow.NewRemoteStyle));
            Layout(window);
            Assert.Equal("Add Connector", window.Title);
            Assert.Equal(Visibility.Visible, window.FormBody.Visibility);
            Assert.Equal(Visibility.Collapsed, window.JsonBody.Visibility);
            Assert.Equal(Visibility.Visible, window.TypePicker.Visibility);
            Assert.Equal(Visibility.Visible, window.RemoteSection.Visibility);
            Assert.Equal(Visibility.Collapsed, window.LocalSection.Visibility);
            Assert.False(window.Model.CanSave);
        });
    }

    [Fact]
    public void ExistingLocalTargetInJsonViewShowsTheJsonEditor()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var config = JsonValue.Object(("command", JsonValue.String("node")), ("args", JsonValue.Array([JsonValue.String("x.js")])), ("env", JsonValue.Object(("K", JsonValue.String("v")))));
        var target = EditTarget.Existing("local", new McpEntry(true, config, EditView.Json));
        WpfApp.Invoke(() =>
        {
            var window = new EditorWindow(state, target);
            Layout(window);
            Assert.Equal("Edit “local”", window.Title);
            Assert.Equal(Visibility.Collapsed, window.FormBody.Visibility);
            Assert.Equal(Visibility.Visible, window.JsonBody.Visibility);
            Assert.Equal(config.EditorText(), window.JsonEditor.Text);

            window.Model.RequestView(EditView.Form);
            Layout(window);
            Assert.Equal(Visibility.Visible, window.FormBody.Visibility);
            Assert.Equal(Visibility.Collapsed, window.TypePicker.Visibility);
            Assert.Equal(Visibility.Visible, window.LocalSection.Visibility);
            Assert.Single(window.ArgList.Items);   // Assert.Equal(1, ….Count) is xUnit2013, an error here
            Assert.Single(window.EnvList.Items);
        });
    }

    [Fact]
    public void ExistingBareRemoteShowsTheRemoteFormWithoutTheTypePicker()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        WpfApp.Invoke(() =>
        {
            var window = new EditorWindow(state, EditTarget.Existing("scoutbook", state.Store.Mcps["scoutbook"]));
            Layout(window);
            Assert.Equal(Visibility.Collapsed, window.TypePicker.Visibility);
            Assert.Equal(Visibility.Visible, window.RemoteSection.Visibility);
            Assert.True(window.Model.CanSave);
            Assert.True(window.Model.CanRemove);
        });
    }

    /// <summary>
    /// The C1 regression, end to end: Add Connector ▸ Bearer token ▸ type a token used to leave
    /// EditorModel.BearerToken empty, so Save answered "Enter a bearer token." and the app's
    /// headline feature was unusable on a first run.
    /// </summary>
    [Fact]
    public void TypingABearerTokenOnANewConnectorReachesTheModelAndSaves()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        WpfApp.Invoke(() =>
        {
            var window = new EditorWindow(state, EditTarget.NewRemote(EditorWindow.NewRemoteStyle));
            Layout(window);
            window.Model.Name = "example";
            window.Model.RemoteUrl = "https://example.com/mcp";
            window.Model.AuthKindIndex = 1;   // Bearer token
            Layout(window);
            Assert.Equal("", window.BearerTokenBox.Password);

            window.BearerTokenBox.Password = "sk-typed-in-the-box";
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

            Assert.Equal("sk-typed-in-the-box", window.Model.BearerToken);
            Assert.True(window.Model.CanSave);
            Assert.True(window.Model.Save());
            Assert.Null(window.Model.ValidationError);
            // Save queues the window's Close; run it inside this guarded body rather than
            // leaving it to fire on the shared host during some later test.
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        });
        Assert.Contains("sk-typed-in-the-box", h.StoreOnDisk().Mcps["example"].Config.EditorText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// M16, the same bridge from a DataTemplate: a masked env value that is empty on disk. The
    /// template-created PasswordBox has no local value for the attached property, so the bridge
    /// recognises it by the property having a value at all, whatever its precedence.
    /// </summary>
    [Fact]
    public void TypingIntoAMaskedEnvRowThatWasEmptyOnDiskReachesTheRow()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var config = JsonValue.Object(("command", JsonValue.String("node")), ("env", JsonValue.Object(("TOKEN", JsonValue.String("")))));
        var target = EditTarget.Existing("local", new McpEntry(true, config, EditView.Form));
        WpfApp.Invoke(() =>
        {
            var window = new EditorWindow(state, target);
            Layout(window);
            var row = Assert.Single(window.Model.EnvRows);
            Assert.False(row.Revealed);   // a stored value is masked until revealed
            Assert.Equal("", row.Value);

            var container = window.EnvList.ItemContainerGenerator.ContainerFromItem(row);
            Assert.NotNull(container);
            var box = FindDescendant<PasswordBox>(container);
            Assert.NotNull(box);

            box.Password = "typed-into-the-mask";
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.Equal("typed-into-the-mask", row.Value);
        });
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }
            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }
        return null;
    }

    /// <summary>
    /// Task 7 review controller addition: EditorModel.IsFormView/IsJsonView refuse a switch by
    /// raising PropertyChanged synchronously inside their own setter, which a WPF TwoWay binding
    /// ignores for the property it is currently writing — so without EditorWindow's explicit
    /// DataBind-priority refresh, the segmented control would stay on Form after a refused switch.
    /// </summary>
    [Fact]
    public void FormToggleSnapsBackWhenTheModelRefusesTheSwitchFromJson()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var config = JsonValue.Object(("command", JsonValue.String("node")));
        var target = EditTarget.Existing("local", new McpEntry(true, config, EditView.Json));
        WpfApp.Invoke(() =>
        {
            var window = new EditorWindow(state, target);
            Layout(window);
            Assert.True(window.JsonToggle.IsChecked);

            window.Model.JsonText = "{ not valid json";   // unmappable: PasteRecovery.Recover returns null
            window.FormToggle.IsChecked = true;            // through the UI element, like a click
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);   // pump the queued refresh

            Assert.False(window.FormToggle.IsChecked);
            Assert.True(window.JsonToggle.IsChecked);
            Assert.True(window.Model.IsJsonView);
        });
    }
}

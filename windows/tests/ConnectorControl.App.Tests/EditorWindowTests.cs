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
    private static void Layout(Window window) => WindowTestSupport.Layout(window, new Size(540, 620));

    /// <summary>
    /// Subscribes the harness's state to the sample document on disk, so "Data team" is a real
    /// synced collection: dbt with a marker in one env value and a shared value in the other,
    /// ledger with a marker in its one argument.
    /// </summary>
    private static void SubscribeToDataTeam(AppStateHarness h, AppState state)
    {
        var path = h.Dir.File(Path.Combine("shared", "data-team.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, CollectionDocumentSamples.DataTeam.Serialize());
        Assert.Null(state.Subscribe(path, null));
    }

    /// <summary>A named collection's copy of a connector, read out of the store the way the
    /// Collections window's row does.</summary>
    private static EditTarget In(AppState state, string collection, string name) =>
        EditTarget.Existing(name, state.Store.Collections[collection].Mcps[name], collection);

    /// <summary>
    /// A named element inside a generated row. A DataTemplate's names live in the template's own
    /// namescope, out of the window's reach, but every element still carries its Name — so a walk
    /// of the row's own visual tree finds it.
    /// </summary>
    private static T RowElement<T>(ItemsControl list, object item, string name) where T : FrameworkElement
    {
        list.UpdateLayout();
        var container = list.ItemContainerGenerator.ContainerFromItem(item);
        Assert.NotNull(container);
        var found = Named<T>(container, name);
        Assert.NotNull(found);
        return found;
    }

    private static T? Named<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name)
            {
                return match;
            }
            if (Named<T>(child, name) is { } deeper)
            {
                return deeper;
            }
        }
        return null;
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
    /// End to end: Add Connector ▸ Bearer token ▸ type a token used to leave
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
    /// The same bridge from a DataTemplate: a masked env value that is empty on disk. The
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
            window.Show();   // an ItemsControl generates no containers until the window has a real layout pass
            Layout(window);
            window.EnvList.UpdateLayout();
            var row = Assert.Single(window.Model.EnvRows);
            Assert.False(row.Revealed);   // a stored value is masked until revealed
            Assert.Equal("", row.Value);

            var container = window.EnvList.ItemContainerGenerator.ContainerFromItem(row);
            Assert.NotNull(container);
            var box = VisualTree.FindDescendant<PasswordBox>(container);
            Assert.NotNull(box);

            box.Password = "typed-into-the-mask";
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.Equal("typed-into-the-mask", row.Value);
            window.Close();
        });
    }

    /// <summary>
    /// EditorModel.View refuses a switch (invalid JSON here) by raising PropertyChanged for View
    /// regardless, so the EnumToBoolConverter-bound RadioButtons re-read it and the segmented
    /// control snaps back to the view the model actually stayed on.
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
            Assert.Equal(EditView.Json, window.Model.View);
        });
    }

    /// <summary>
    /// Spec 2026-09-05-tool-probe §3.4: a missing npx puts the caution note under Server URL and
    /// leaves Save alone.
    /// </summary>
    [Fact]
    public void MissingNpxShowsTheToolNoteUnderTheServerUrlField()
    {
        using var h = new AppStateHarness();
        h.Tools.Statuses[Tool.Npx] = ToolStatus.NotFound;
        using var state = h.Create();
        WpfApp.Invoke(() =>
        {
            var window = new EditorWindow(state, EditTarget.NewRemote(EditorWindow.NewRemoteStyle));
            Layout(window);
            Assert.Equal(Visibility.Collapsed, window.RemoteToolNote.Visibility);   // not probed yet
            Assert.True(h.Ui.PumpUntil(() => window.Model.HasToolNote, TimeSpan.FromSeconds(5)));
            Layout(window);
            Assert.Equal(Visibility.Visible, window.RemoteToolNote.Visibility);
            Assert.NotNull(window.RemoteToolNote.Note);
            Assert.Equal("npx wasn’t found, so Claude Desktop won’t be able to start this connector.", window.RemoteToolNote.Note.Text);
            Assert.Equal("winget install OpenJS.NodeJS.LTS", window.RemoteToolNote.Note.InstallCommand);
            Assert.Equal(Visibility.Visible, window.RemoteToolNote.TextLine.Visibility);
            Assert.Equal(Visibility.Collapsed, window.LocalSection.Visibility);
            window.Model.RemoteUrl = "https://example.com/mcp";
            Assert.True(window.Model.CanSave);   // the note never blocks Save
            window.Close();
        });
    }

    /// <summary>
    /// A connector in a synced collection opens with its author's fields dead and only the values
    /// the document asks this machine for live — including the env row's name and the row
    /// structure, because the read-only save projection matches leaves by pointer. The on/off
    /// switch is not in this window at all (the flyout row owns it), so what the editor has to
    /// keep working is Save.
    /// </summary>
    [Fact]
    public void ASyncedConnectorLocksEverythingButPlaceholdersAndTheSwitch()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        SubscribeToDataTeam(h, state);
        WpfApp.Invoke(() =>
        {
            var window = new EditorWindow(state, In(state, "Data team", "dbt"));
            window.Show();   // an ItemsControl generates no containers until the window has a real layout pass
            Layout(window);

            Assert.True(window.Model.IsReadOnly);
            Assert.False(window.NameBox.IsEnabled);
            Assert.False(window.CommandBox.IsEnabled);
            Assert.False(window.AddArgButton.IsEnabled);
            Assert.False(window.AddEnvButton.IsEnabled);
            // Make Local Copy… takes Remove's slot, and Save stays live for the placeholder.
            Assert.False(window.Model.CanRemove);
            Assert.Equal(Visibility.Visible, window.MakeLocalCopyButton.Visibility);
            Assert.True(window.Model.CanSave);

            var token = window.Model.EnvRows.Single(r => r.Name == "DBT_TOKEN");
            var asked = RowElement<TextBox>(window.EnvList, token, "EnvPlaceholder");
            Assert.Equal(Visibility.Visible, asked.Visibility);
            Assert.True(asked.IsEnabled);
            Assert.Equal(EditorModel.NeedsValue, asked.Tag);
            Assert.Equal(Visibility.Collapsed, RowElement<ContentControl>(window.EnvList, token, "EnvValue").Visibility);
            Assert.False(RowElement<TextBox>(window.EnvList, token, "EnvName").IsEnabled);
            var hint = RowElement<TextBlock>(window.EnvList, token, "EnvHint");
            Assert.Equal(Visibility.Visible, hint.Visibility);
            Assert.Equal("cloud.getdbt.com ▸ API tokens", hint.Text);
            Assert.Equal(new Thickness(1), RowElement<Border>(window.EnvList, token, "EnvMark").BorderThickness);

            var region = window.Model.EnvRows.Single(r => r.Name == "DBT_REGION");
            // A shared value asks for nothing, so it is simply locked.
            Assert.Equal(Visibility.Collapsed, RowElement<TextBox>(window.EnvList, region, "EnvPlaceholder").Visibility);
            var shared = RowElement<ContentControl>(window.EnvList, region, "EnvValue");
            Assert.Equal(Visibility.Visible, shared.Visibility);
            Assert.False(shared.IsEnabled);
            Assert.Equal(Visibility.Collapsed, RowElement<TextBlock>(window.EnvList, region, "EnvHint").Visibility);
            Assert.Equal(new Thickness(0), RowElement<Border>(window.EnvList, region, "EnvMark").BorderThickness);

            // Filling the value clears the mark and the hint, and leaves the field it went into
            // exactly where it was: the model's placeholder flag is live, so a field that asked
            // at open has to stay the live one whatever is typed into it.
            token.Value = "dbt_pat_123";
            Layout(window);
            Assert.False(window.Model.IsPlaceholder(token));
            Assert.Equal(Visibility.Visible, RowElement<TextBox>(window.EnvList, token, "EnvPlaceholder").Visibility);
            Assert.True(RowElement<TextBox>(window.EnvList, token, "EnvPlaceholder").IsEnabled);
            Assert.Equal(Visibility.Collapsed, RowElement<TextBlock>(window.EnvList, token, "EnvHint").Visibility);
            Assert.Equal(new Thickness(0), RowElement<Border>(window.EnvList, token, "EnvMark").BorderThickness);

            // The JSON view of somebody else's connector is a reading view, and the paste tip
            // offers something it cannot do.
            window.Model.RequestView(EditView.Json);
            Layout(window);
            Assert.True(window.JsonEditor.IsReadOnly);
            Assert.Equal(Visibility.Collapsed, window.JsonTipLine.Visibility);
            window.Close();

            // ledger's one argument is the marker, so that is the live field in an otherwise
            // dead form.
            var ledger = new EditorWindow(state, In(state, "Data team", "ledger"));
            ledger.Show();
            Layout(ledger);
            var arg = Assert.Single(ledger.Model.Args);
            var path = RowElement<TextBox>(ledger.ArgList, arg, "ArgPlaceholder");
            Assert.Equal(Visibility.Visible, path.Visibility);
            Assert.True(path.IsEnabled);
            Assert.Equal(EditorModel.NeedsPath, path.Tag);
            Assert.Equal(Visibility.Collapsed, RowElement<TextBox>(ledger.ArgList, arg, "ArgValue").Visibility);
            Assert.Equal("your ledger clone, then dist/index.js", RowElement<TextBlock>(ledger.ArgList, arg, "ArgHint").Text);
            Assert.Equal(new Thickness(1), RowElement<Border>(ledger.ArgList, arg, "ArgMark").BorderThickness);
            Assert.False(ledger.CommandBox.IsEnabled);
            ledger.Close();
        });
    }

    /// <summary>
    /// One header line per kind of collection, and none at all for an ordinary local connector —
    /// plus the repaint a collection that stops syncing under an open editor has to cause.
    /// </summary>
    [Fact]
    public void HeaderStatesRenderPerCollection()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        SubscribeToDataTeam(h, state);
        var folder = h.Dir.File("share");
        Directory.CreateDirectory(folder);
        // Every state change runs on the host's dispatcher: an open editor watches AppState, and
        // its handler touches the window.
        WpfApp.Invoke(() =>
        {
            var local = new EditorWindow(state, In(state, "Default", "scoutbook"));
            Layout(local);
            Assert.Equal(Visibility.Collapsed, local.CollectionHeader.Visibility);
            local.Close();

            var synced = new EditorWindow(state, In(state, "Data team", "dbt"));
            Layout(synced);
            Assert.Equal(Visibility.Visible, synced.CollectionHeader.Visibility);
            Assert.Equal(Visibility.Visible, synced.SyncedHeader.Visibility);
            Assert.Equal(Visibility.Collapsed, synced.PublishedHeader.Visibility);
            Assert.Equal(Visibility.Collapsed, synced.ImportedHeader.Visibility);
            Assert.Equal("Synced from Data team · read-only", synced.Model.HeaderNote);
            Assert.Equal(EditorModel.WhatCanIChange, synced.WhatCanIChange.Content);
            synced.Close();

            Assert.Null(state.MakeLocalCopy(["dbt"], "Data team", "Default"));
            var imported = new EditorWindow(state, In(state, "Default", "dbt"));
            Layout(imported);
            Assert.Equal(Visibility.Visible, imported.ImportedHeader.Visibility);
            Assert.Equal(Visibility.Collapsed, imported.SyncedHeader.Visibility);
            Assert.Equal(imported.Model.HeaderNote, imported.ImportedHeader.Text);
            // A copy is the user's own.
            Assert.False(imported.Model.IsReadOnly);
            Assert.True(imported.NameBox.IsEnabled);
            imported.Close();

            Assert.Null(state.CreateCollection("Team"));
            Assert.Null(state.StartPublishing("Team", folder, PublishIntent.None));
            var published = new EditorWindow(state, In(state, "Team", "scoutbook"));
            Layout(published);
            Assert.Equal(Visibility.Visible, published.PublishedHeader.Visibility);
            Assert.Equal(Visibility.Collapsed, published.SyncedHeader.Visibility);
            // Publishing locks nothing: the file is the author's own.
            Assert.True(published.NameBox.IsEnabled);
            published.Close();

            // The editor model republishes on tool statuses alone, so the window itself has to
            // watch AppState: a collection that stops syncing under an open editor repaints it.
            var open = new EditorWindow(state, In(state, "Data team", "dbt"));
            Layout(open);
            Assert.Equal(Visibility.Visible, open.SyncedHeader.Visibility);
            Assert.False(open.NameBox.IsEnabled);

            state.StopSyncing("Data team");
            Layout(open);
            Assert.False(open.Model.IsReadOnly);
            Assert.Equal(Visibility.Collapsed, open.SyncedHeader.Visibility);
            Assert.True(open.NameBox.IsEnabled);
            open.Close();
        });
    }

    /// <summary>
    /// The checkbox above the buttons appears only when another local collection holds a
    /// byte-identical copy of this connector, and it is off until the user ticks it.
    /// </summary>
    [Fact]
    public void ThePropagateLineAppearsOnlyWithATwin()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        WpfApp.Invoke(() =>
        {
            var alone = new EditorWindow(state, In(state, "Default", "scoutbook"));
            Layout(alone);
            Assert.Empty(alone.Model.PropagateTargets);
            Assert.Equal(Visibility.Collapsed, alone.PropagateLine.Visibility);
            alone.Close();

            // A second local collection, copied from this one, holds the same connector.
            Assert.Null(state.CreateCollection("Backup"));
            state.SwitchCollection("Default");

            var twinned = new EditorWindow(state, In(state, "Default", "scoutbook"));
            Layout(twinned);
            Assert.Equal(Visibility.Visible, twinned.PropagateLine.Visibility);
            Assert.Equal("Also apply this change to Backup, which has an identical scoutbook", twinned.Model.PropagateMessage);
            Assert.False(twinned.PropagateLine.IsChecked);

            twinned.PropagateLine.IsChecked = true;
            twinned.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.True(twinned.Model.Propagate);
            twinned.Close();
        });
    }

    /// <summary>
    /// Make Local Copy… hands one local collection to the model and closes: what the user came
    /// for now lives somewhere they can change it.
    /// </summary>
    [Fact]
    public void MakeLocalCopyClosesAfterCopying()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        SubscribeToDataTeam(h, state);
        WpfApp.Invoke(() =>
        {
            var window = new EditorWindow(state, In(state, "Data team", "notion"));
            window.Show();
            Layout(window);
            Assert.Equal(Visibility.Visible, window.MakeLocalCopyButton.Visibility);

            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.MakeLocalCopy("Default");

            Assert.True(closed);
            // The copy carries its unfilled marker, exactly as it stands.
            Assert.Equal(state.Store.Collections["Data team"].Mcps["notion"].Config,
                         state.Store.Collections["Default"].Mcps["notion"].Config);
            Assert.Equal("Data team", state.CollectionsFile.Collections["Default"].Provenance["notion"].From);
        });
    }
}

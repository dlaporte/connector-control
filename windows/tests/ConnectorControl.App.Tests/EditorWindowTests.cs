using System.Windows;
using System.Windows.Controls;
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
    /// One editor window, closed however the body ends, and never both visible and active while it
    /// lives.
    ///
    /// Every App test funnels through the one WPF application's dispatcher, xUnit runs test
    /// classes in parallel, and <see cref="Layout"/> pumps that dispatcher down to DataBind
    /// priority — which is where another class's queued WpfApp.Invoke body gets to run, nested
    /// inside this one. A window of ours that is visible AND active during such a pump is the
    /// answer WpfDialogs.ResolveOwner() gives, and DialogTests asserts there is none. So a window
    /// that needs a real layout pass — an ItemsControl generates no containers without one — is
    /// shown without taking the activation, and hidden again the moment it has had its pass. The
    /// containers it generated outlive the hiding.
    /// </summary>
    /// <param name="rows">True when the body reaches into a generated ItemsControl row.</param>
    private static void Editing(AppState state, EditTarget target, Action<EditorWindow> body, bool rows = false)
    {
        var window = new EditorWindow(state, target) { ShowActivated = false };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            if (rows)
            {
                // Bindings settle first, while nothing of ours is on screen, because that is the
                // step that pumps. Between Show and Hide there is no pump at all: UpdateLayout
                // runs the real layout pass — the one that generates the rows — synchronously.
                Layout(window);
                window.Show();
                window.UpdateLayout();
                window.Hide();
            }
            Layout(window);
            body(window);
        }
        finally
        {
            if (!closed)
            {
                window.Close();
            }
        }
    }

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

    /// <summary>Every visual ancestor of an element, with what could hide or disable it.</summary>
    private static string Chain(DependencyObject element)
    {
        var parts = new List<string>();
        for (var node = element; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            parts.Add(node is UIElement ui ? $"{node.GetType().Name}({ui.Visibility},{ui.IsEnabled})" : node.GetType().Name);
        }
        return string.Join(" < ", parts);
    }

    [Fact]
    public void TheKeyboardStartsInTheFirstEditableField()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        // The name, as on the Mac, where the window's first text field takes the keyboard.
        WpfApp.Invoke(() => Editing(state, EditTarget.NewRemote(EditorWindow.NewRemoteStyle, "Default"), window =>
        {
            var found = InitialFocus.FirstField(window);
            Assert.True(ReferenceEquals(window.NameBox, found), $"found {found?.GetType().Name ?? "nothing"}; the name box's chain: {Chain(window.NameBox)}");
        }, rows: true));
    }

    [Fact]
    public void NewRemoteTargetShowsTheRemoteFormWithTheTypePicker()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        WpfApp.Invoke(() => Editing(state, EditTarget.NewRemote(EditorWindow.NewRemoteStyle, "Default"), window =>
        {
            Assert.Equal("Add Connector", window.Title);
            Assert.Equal(Visibility.Visible, window.FormBody.Visibility);
            Assert.Equal(Visibility.Collapsed, window.JsonBody.Visibility);
            Assert.Equal(Visibility.Visible, window.TypePicker.Visibility);
            Assert.Equal(Visibility.Visible, window.RemoteSection.Visibility);
            Assert.Equal(Visibility.Collapsed, window.LocalSection.Visibility);
            Assert.False(window.Model.CanSave);
        }));
    }

    [Fact]
    public void ExistingLocalTargetInJsonViewShowsTheJsonEditor()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        var config = JsonValue.Object(("command", JsonValue.String("node")), ("args", JsonValue.Array([JsonValue.String("x.js")])), ("env", JsonValue.Object(("K", JsonValue.String("v")))));
        var target = EditTarget.Existing("local", new McpEntry(true, config, EditView.Json), "Default");
        WpfApp.Invoke(() => Editing(state, target, window =>
        {
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
        }));
    }

    [Fact]
    public void ExistingBareRemoteShowsTheRemoteFormWithoutTheTypePicker()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        WpfApp.Invoke(() => Editing(state, EditTarget.Existing("scoutbook", state.Store.Mcps["scoutbook"], "Default"), window =>
        {
            Assert.Equal(Visibility.Collapsed, window.TypePicker.Visibility);
            Assert.Equal(Visibility.Visible, window.RemoteSection.Visibility);
            Assert.True(window.Model.CanSave);
        }));
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
        WpfApp.Invoke(() => Editing(state, EditTarget.NewRemote(EditorWindow.NewRemoteStyle, "Default"), window =>
        {
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
        }));
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
        var target = EditTarget.Existing("local", new McpEntry(true, config, EditView.Form), "Default");
        WpfApp.Invoke(() => Editing(state, target, window =>
        {
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
        }, rows: true));
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
        var target = EditTarget.Existing("local", new McpEntry(true, config, EditView.Json), "Default");
        WpfApp.Invoke(() => Editing(state, target, window =>
        {
            Assert.True(window.JsonToggle.IsChecked);

            window.Model.JsonText = "{ not valid json";   // unmappable: PasteRecovery.Recover returns null
            window.FormToggle.IsChecked = true;            // through the UI element, like a click
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);   // pump the queued refresh

            Assert.False(window.FormToggle.IsChecked);
            Assert.True(window.JsonToggle.IsChecked);
            Assert.Equal(EditView.Json, window.Model.View);
        }));
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
        WpfApp.Invoke(() => Editing(state, EditTarget.NewRemote(EditorWindow.NewRemoteStyle, "Default"), window =>
        {
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
        }));
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
            Editing(state, In(state, "Data team", "dbt"), window =>
            {
                Assert.True(window.Model.IsReadOnly);
                Assert.False(window.NameBox.IsEnabled);
                Assert.False(window.CommandBox.IsEnabled);
                Assert.False(window.AddArgButton.IsEnabled);
                Assert.False(window.AddEnvButton.IsEnabled);
                // Save stays live for the placeholder.
                Assert.True(window.Model.CanSave);

                var token = window.Model.EnvRows.Single(r => r.Name == "DBT_TOKEN");
                var asked = RowElements.Find<TextBox>(window.EnvList, token, "EnvPlaceholder");
                Assert.Equal(Visibility.Visible, asked.Visibility);
                Assert.True(asked.IsEnabled);
                Assert.Equal(EditorModel.NeedsValue, asked.Tag);
                Assert.Equal(Visibility.Collapsed, RowElements.Find<ContentControl>(window.EnvList, token, "EnvValue").Visibility);
                Assert.False(RowElements.Find<TextBox>(window.EnvList, token, "EnvName").IsEnabled);
                // The field holds the marker, so the phrase that names the state is a caution
                // line under it, not a watermark nothing empty would show.
                Assert.Equal(Visibility.Visible, RowElements.Find<StackPanel>(window.EnvList, token, "EnvNeeds").Visibility);
                var hint = RowElements.Find<TextBlock>(window.EnvList, token, "EnvHint");
                Assert.Equal(Visibility.Visible, hint.Visibility);
                Assert.Equal("cloud.getdbt.com ▸ API tokens", hint.Text);
                Assert.Equal(new Thickness(1), RowElements.Find<Border>(window.EnvList, token, "EnvMark").BorderThickness);

                var region = window.Model.EnvRows.Single(r => r.Name == "DBT_REGION");
                // A shared value asks for nothing, so it is simply locked.
                Assert.Equal(Visibility.Collapsed, RowElements.Find<TextBox>(window.EnvList, region, "EnvPlaceholder").Visibility);
                var shared = RowElements.Find<ContentControl>(window.EnvList, region, "EnvValue");
                Assert.Equal(Visibility.Visible, shared.Visibility);
                Assert.False(shared.IsEnabled);
                Assert.Equal(Visibility.Collapsed, RowElements.Find<StackPanel>(window.EnvList, region, "EnvNeeds").Visibility);
                Assert.Equal(Visibility.Collapsed, RowElements.Find<TextBlock>(window.EnvList, region, "EnvHint").Visibility);
                Assert.Equal(new Thickness(0), RowElements.Find<Border>(window.EnvList, region, "EnvMark").BorderThickness);

                // Filling the value clears the mark and the hint, and leaves the field it went
                // into exactly where it was: the model's placeholder flag is live, so a field that
                // asked at open has to stay the live one whatever is typed into it.
                token.Value = "dbt_pat_123";
                Layout(window);
                Assert.False(window.Model.IsPlaceholder(token));
                Assert.Equal(Visibility.Visible, RowElements.Find<TextBox>(window.EnvList, token, "EnvPlaceholder").Visibility);
                Assert.True(RowElements.Find<TextBox>(window.EnvList, token, "EnvPlaceholder").IsEnabled);
                Assert.Equal(Visibility.Collapsed, RowElements.Find<StackPanel>(window.EnvList, token, "EnvNeeds").Visibility);
                Assert.Equal(Visibility.Collapsed, RowElements.Find<TextBlock>(window.EnvList, token, "EnvHint").Visibility);
                Assert.Equal(new Thickness(0), RowElements.Find<Border>(window.EnvList, token, "EnvMark").BorderThickness);

                // Emptying it does not settle the debt: the marker is gone but the value is still
                // owed, so the mark and the phrase come back and the box stays the live one.
                token.Value = "";
                Layout(window);
                Assert.Equal(Visibility.Visible, RowElements.Find<StackPanel>(window.EnvList, token, "EnvNeeds").Visibility);
                Assert.Equal(new Thickness(1), RowElements.Find<Border>(window.EnvList, token, "EnvMark").BorderThickness);
                Assert.Equal(Visibility.Visible, RowElements.Find<TextBox>(window.EnvList, token, "EnvPlaceholder").Visibility);

                // The JSON view of somebody else's connector is a reading view, and the paste tip
                // offers something it cannot do.
                window.Model.RequestView(EditView.Json);
                Layout(window);
                Assert.True(window.JsonEditor.IsReadOnly);
                Assert.Equal(Visibility.Collapsed, window.JsonTipLine.Visibility);
            }, rows: true);

            // ledger's one argument is the marker, so that is the live field in an otherwise
            // dead form.
            Editing(state, In(state, "Data team", "ledger"), ledger =>
            {
                var arg = Assert.Single(ledger.Model.Args);
                var path = RowElements.Find<TextBox>(ledger.ArgList, arg, "ArgPlaceholder");
                Assert.Equal(Visibility.Visible, path.Visibility);
                Assert.True(path.IsEnabled);
                Assert.Equal(EditorModel.NeedsPath, path.Tag);
                Assert.Equal(Visibility.Collapsed, RowElements.Find<TextBox>(ledger.ArgList, arg, "ArgValue").Visibility);
                Assert.Equal(Visibility.Visible, RowElements.Find<StackPanel>(ledger.ArgList, arg, "ArgNeeds").Visibility);
                Assert.Equal("your ledger clone, then dist/index.js", RowElements.Find<TextBlock>(ledger.ArgList, arg, "ArgHint").Text);
                Assert.Equal(new Thickness(1), RowElements.Find<Border>(ledger.ArgList, arg, "ArgMark").BorderThickness);
                Assert.False(ledger.CommandBox.IsEnabled);
            }, rows: true);
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
            Editing(state, In(state, "Default", "scoutbook"), local =>
                Assert.Equal(Visibility.Collapsed, local.CollectionHeader.Visibility));

            Editing(state, In(state, "Data team", "dbt"), synced =>
            {
                Assert.Equal(Visibility.Visible, synced.CollectionHeader.Visibility);
                Assert.Equal(Visibility.Visible, synced.SyncedHeader.Visibility);
                Assert.Equal(Visibility.Collapsed, synced.PublishedHeader.Visibility);
                Assert.Equal(Visibility.Collapsed, synced.ImportedHeader.Visibility);
                Assert.Equal("Synced from Data team · read-only", synced.SyncedHeaderText.Text);
                Assert.Equal(EditorModel.WhatCanIChange, synced.WhatCanIChange.Content);
            });

            Assert.Null(state.MakeLocalCopy(["dbt"], "Data team", "Default"));
            Editing(state, In(state, "Default", "dbt"), imported =>
            {
                Assert.Equal(Visibility.Visible, imported.ImportedHeader.Visibility);
                Assert.Equal(Visibility.Collapsed, imported.SyncedHeader.Visibility);
                Assert.Equal(imported.Model.HeaderNote, imported.ImportedHeader.Text);
                // A copy is the user's own.
                Assert.False(imported.Model.IsReadOnly);
                Assert.True(imported.NameBox.IsEnabled);
            });

            Assert.Null(state.CreateCollection("Team"));
            Assert.Null(state.StartPublishing("Team", folder, PublishIntent.None));
            Editing(state, In(state, "Team", "scoutbook"), published =>
            {
                Assert.Equal(Visibility.Visible, published.PublishedHeader.Visibility);
                Assert.Equal(Visibility.Collapsed, published.SyncedHeader.Visibility);
                Assert.Equal(published.Model.HeaderNote, published.PublishedHeaderText.Text);
                Assert.StartsWith($"Published to {folder}", published.PublishedHeaderText.Text, StringComparison.Ordinal);
                // Publishing locks nothing: the file is the author's own.
                Assert.True(published.NameBox.IsEnabled);
            });

            // What the author told their readers to put in place of the value publishing strips
            // shows beside that value here, where the value itself stays.
            var hints = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["dbt"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["DBT_REGION"] = "the region your account is in" },
            };
            Assert.Null(state.UpdatePublishIntent("Team", new PublishIntent([], [], hints)));
            Editing(state, In(state, "Team", "dbt"), author =>
            {
                Assert.True(author.Model.HasPublishedHints);
                var region = author.Model.EnvRows.Single(r => r.Name == "DBT_REGION");
                Assert.Equal("the region your account is in",
                             RowElements.Find<TextBlock>(author.EnvList, region, "EnvPublishedHint").Text);
                // A shared value is nobody's debt, so the caution block stays away.
                Assert.Equal(Visibility.Collapsed, RowElements.Find<StackPanel>(author.EnvList, region, "EnvNeeds").Visibility);
                // And a variable the author said nothing about shows nothing.
                var token = author.Model.EnvRows.Single(r => r.Name == "DBT_TOKEN");
                Assert.Equal(Visibility.Collapsed, RowElements.Find<TextBlock>(author.EnvList, token, "EnvPublishedHint").Visibility);
            }, rows: true);

            // A collection that stops syncing under an open editor repaints it, without the
            // window watching AppState itself: the model raises what the collection decides.
            Editing(state, In(state, "Data team", "dbt"), open =>
            {
                Assert.Equal(Visibility.Visible, open.SyncedHeader.Visibility);
                Assert.False(open.NameBox.IsEnabled);

                // A value the user fills in while the form is still the author's.
                var token = open.Model.EnvRows.Single(r => r.Name == "DBT_TOKEN");
                Assert.Equal(Visibility.Visible, RowElements.Find<TextBox>(open.EnvList, token, "EnvPlaceholder").Visibility);
                token.Value = "dbt_pat_123";
                Layout(open);

                // A second window on the same collection, whose marker nobody fills: the two
                // halves of the retake only show up together.
                Editing(state, In(state, "Data team", "ledger"), ledger =>
                {
                    var arg = Assert.Single(ledger.Model.Args);
                    Assert.Equal("your ledger clone, then dist/index.js",
                                 RowElements.Find<TextBlock>(ledger.ArgList, arg, "ArgHint").Text);

                    state.StopSyncing("Data team");
                    Layout(open);
                    Layout(ledger);

                    Assert.False(open.Model.IsReadOnly);
                    Assert.Equal(Visibility.Collapsed, open.SyncedHeader.Visibility);
                    Assert.True(open.NameBox.IsEnabled);
                    // The model retook its snapshot, so the filled value is an ordinary secret
                    // again and goes back behind the mask rather than staying in the clear.
                    Assert.False(open.Model.AsksFor(token));
                    Assert.Equal(Visibility.Collapsed, RowElements.Find<TextBox>(open.EnvList, token, "EnvPlaceholder").Visibility);
                    Assert.Equal(Visibility.Visible, RowElements.Find<ContentControl>(open.EnvList, token, "EnvValue").Visibility);

                    // ledger's marker survives, so its box stays the live one — but the needs the
                    // hint came from went with the subscription, and the hint must not outlive them.
                    Assert.True(ledger.Model.AsksForArg(0));
                    Assert.Equal(Visibility.Visible, RowElements.Find<TextBox>(ledger.ArgList, arg, "ArgPlaceholder").Visibility);
                    Assert.Equal("", RowElements.Find<TextBlock>(ledger.ArgList, arg, "ArgHint").Text);
                }, rows: true);
            }, rows: true);
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
            Editing(state, In(state, "Default", "scoutbook"), alone =>
            {
                Assert.Empty(alone.Model.PropagateTargets);
                Assert.Equal(Visibility.Collapsed, alone.PropagateLine.Visibility);
            });

            // A second local collection, copied from this one, holds the same connector.
            Assert.Null(state.CreateCollection("Backup"));
            state.SwitchCollection("Default");

            Editing(state, In(state, "Default", "scoutbook"), twinned =>
            {
                Assert.Equal(Visibility.Visible, twinned.PropagateLine.Visibility);
                Assert.Equal("Also apply this change to Backup, which has an identical scoutbook", twinned.Model.PropagateMessage);
                Assert.False(twinned.PropagateLine.IsChecked);

                twinned.PropagateLine.IsChecked = true;
                twinned.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Assert.True(twinned.Model.Propagate);
            });
        });
    }
}

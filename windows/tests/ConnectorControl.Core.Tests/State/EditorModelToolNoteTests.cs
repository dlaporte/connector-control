using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

public class EditorModelToolNoteTests
{
    private const string Url = "https://scoutbook.example.com/mcp";

    /// <summary>AdoptForm assigns Command, Args and IsRemote one after another; with evaluation
    /// suppressed until the end, a config whose tool is unchanged costs no probe at all, and a
    /// changed one costs exactly one batch.</summary>
    [Fact]
    public void AdoptingAFormFromJsonEvaluatesTheToolOnceAtTheEnd()
    {
        using var rig = new EditorRig();
        using var editor = rig.Editor(EditTarget.New(rig.Local("node", ["server.js"])));
        Assert.True(rig.H.Ui.PumpUntil(() => rig.State.ToolStatuses.ContainsKey(Tool.Node), TimeSpan.FromSeconds(5)));
        var batches = rig.H.Tools.Batches;
        editor.RequestView(EditView.Json);
        editor.JsonText = "{\"command\": \"node\", \"args\": [\"other.js\"]}";
        editor.RequestView(EditView.Form);
        Assert.Equal(EditView.Form, editor.View);
        Assert.Equal(Tool.Node, editor.RequiredTool);
        Assert.Equal(batches, rig.H.Tools.Batches);   // same tool after adoption: nothing to probe
        editor.RequestView(EditView.Json);
        editor.JsonText = "{\"command\": \"uvx\", \"args\": [\"tool\"]}";
        Assert.True(rig.H.Ui.PumpUntil(() => rig.H.Tools.Batches == batches + 1, TimeSpan.FromSeconds(5)));   // the JSON view evaluates as it parses
        editor.RequestView(EditView.Form);
        Assert.Equal(Tool.Uvx, editor.RequiredTool);
        Assert.Equal(batches + 1, rig.H.Tools.Batches);   // adoption of an already-evaluated config probes nothing more
    }

    [Fact]
    public void NewRemoteConnectorNotesAMissingNpx()
    {
        using var rig = new EditorRig(h => h.Tools.Statuses[Tool.Npx] = ToolStatus.NotFound);
        using var editor = rig.Editor(EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        Assert.Equal(Tool.Npx, editor.RequiredTool);
        Assert.Null(editor.ToolNote);   // not probed yet: no note, and nothing blocks
        Assert.False(editor.HasToolNote);
        Assert.True(rig.H.Ui.PumpUntil(() => editor.HasToolNote, TimeSpan.FromSeconds(5)));
        Assert.Equal("npx wasn’t found, so Claude Desktop won’t be able to start this connector.", editor.ToolNote!.Text);
        Assert.Equal("Install Node.js", editor.ToolNote.LinkTitle);
        Assert.Equal("https://nodejs.org/en/download", editor.ToolNote.LinkUrl);
        Assert.Equal("winget install OpenJS.NodeJS.LTS", editor.ToolNote.InstallCommand);
        editor.Name = "example";
        editor.RemoteUrl = Url;
        Assert.True(editor.CanSave);   // the note never blocks Save
        Assert.True(editor.Save());
        Assert.Null(editor.ValidationError);
    }

    [Fact]
    public void LocalCommandChangesReEvaluateAndReProbeTheTool()
    {
        using var rig = new EditorRig(h => h.Tools.Statuses[Tool.Uvx] = ToolStatus.NotFound);
        using var editor = rig.Editor(EditTarget.New(rig.Local("node", ["server.js"])));
        Assert.Equal(Tool.Node, editor.RequiredTool);
        Assert.True(rig.H.Ui.PumpUntil(() => rig.State.ToolStatuses.ContainsKey(Tool.Node), TimeSpan.FromSeconds(5)));
        Assert.False(editor.HasToolNote);   // node is installed on this (fake) machine
        var raised = new List<string?>();
        editor.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        editor.Command = "uvx";
        Assert.Equal(Tool.Uvx, editor.RequiredTool);
        Assert.Contains(nameof(EditorModel.RequiredTool), raised);
        Assert.True(rig.H.Ui.PumpUntil(() => editor.HasToolNote, TimeSpan.FromSeconds(5)));
        Assert.Contains(nameof(EditorModel.HasToolNote), raised);
        Assert.StartsWith("uvx wasn’t found", editor.ToolNote!.Text, StringComparison.Ordinal);
        editor.Command = "/usr/local/bin/uvx";   // a path is the user's deliberate choice: no PATH lookup, no note
        Assert.Null(editor.RequiredTool);
        Assert.False(editor.HasToolNote);
        editor.Command = "python";
        Assert.Null(editor.RequiredTool);
        Assert.Equal(2, rig.H.Tools.Probed.Count);   // node once, uvx once — the non-tools cost nothing
        editor.Command = "uvx";
        // Back to a tool that is cached: probed again anyway — it may have been installed meanwhile.
        Assert.True(rig.H.Ui.PumpUntil(() => rig.H.Tools.Probed.Count == 3, TimeSpan.FromSeconds(5)));
        Assert.True(editor.HasToolNote);
    }

    [Fact]
    public void JsonViewEvaluatesTheParsedConfig()
    {
        using var rig = new EditorRig(h => h.Tools.Statuses[Tool.Uv] = ToolStatus.NotFound);
        using var editor = rig.Editor(EditTarget.New(rig.Local("node", ["x.js"])));
        editor.RequestView(EditView.Json);
        Assert.Equal(Tool.Node, editor.RequiredTool);   // the same config, now read from the text
        editor.JsonText = "{\"command\": \"uv\", \"args\": [\"run\", \"server.py\"]}";
        Assert.Equal(Tool.Uv, editor.RequiredTool);
        Assert.True(rig.H.Ui.PumpUntil(() => editor.HasToolNote, TimeSpan.FromSeconds(5)));
        editor.JsonText = "{ not json";
        Assert.Null(editor.RequiredTool);   // unparseable: nothing to evaluate
        Assert.False(editor.HasToolNote);
        editor.JsonText = "{\"command\": \"cmd\", \"args\": [\"/c\", \"npx\", \"-y\", \"mcp-remote\", \"" + Url + "\"]}";
        Assert.Equal(Tool.Npx, editor.RequiredTool);
        editor.RequestView(EditView.Form);   // a bare bridge invocation: the remote form, still npx
        Assert.True(editor.IsRemote);
        Assert.Equal(Tool.Npx, editor.RequiredTool);
    }

    [Fact]
    public void ACachedStatusShowsTheNoteAtOnceAndAFoundToolShowsNone()
    {
        using var rig = new EditorRig(h => h.Tools.Statuses[Tool.Npx] = ToolStatus.NotFound);
        var warm = rig.State.RefreshToolsAsync();
        // The task completes once the probe batch is posted, not once it is published:
        // pump until the cache the editor reads from is actually populated.
        Assert.True(rig.H.Ui.PumpUntil(() => rig.State.ToolStatuses.ContainsKey(Tool.Npx), TimeSpan.FromSeconds(5)));
        Assert.True(warm.IsCompleted);
        var batches = rig.H.Tools.Batches;
        using var remote = rig.Editor(EditTarget.Existing("scoutbook", rig.State.Store.Mcps["scoutbook"]));   // bare npx mcp-remote
        Assert.True(remote.HasToolNote);          // straight from the cache, no wait
        Assert.Equal(batches, rig.H.Tools.Batches);   // and no re-probe on open
        using var local = rig.Editor(EditTarget.Existing("local", new McpEntry(rig.Local("node", ["x.js"]))));
        Assert.Equal(Tool.Node, local.RequiredTool);
        Assert.False(local.HasToolNote);
        Assert.Equal(batches, rig.H.Tools.Batches);
        remote.Dispose();
        rig.State.RefreshToolsAsync([Tool.Npx]);      // a disposed editor no longer listens
        Assert.True(rig.H.Ui.PumpUntil(() => rig.H.Tools.Batches == batches + 1, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void DisposeStopsRelayingAppStateToolStatusChanges()
    {
        using var rig = new EditorRig(h => h.Tools.Statuses[Tool.Npx] = ToolStatus.NotFound);
        using var editor = rig.Editor(EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        Assert.True(rig.H.Ui.PumpUntil(() => editor.HasToolNote, TimeSpan.FromSeconds(5)));

        editor.Dispose();
        // Simulate a bound view: it only re-reads ToolNote when told to by a PropertyChanged event.
        var lastSeenNote = editor.ToolNote;
        var beforeChange = lastSeenNote;
        var raised = new List<string?>();
        editor.PropertyChanged += (_, e) =>
        {
            raised.Add(e.PropertyName);
            lastSeenNote = editor.ToolNote;
        };

        // Publish a change that would clear the note on a live (not disposed) editor.
        rig.H.Tools.Statuses[Tool.Npx] = new ToolStatus(@"C:\fake\npx.cmd", "1.0.0");
        rig.State.RefreshToolsAsync([Tool.Npx]);
        Assert.True(rig.H.Ui.PumpUntil(() => rig.State.ToolStatuses[Tool.Npx].Found, TimeSpan.FromSeconds(5)));

        Assert.Empty(raised);
        Assert.Equal(beforeChange, lastSeenNote);   // never re-read: Dispose stopped the AppState.PropertyChanged relay
    }

    [Fact]
    public void DisposeStopsRelayingArgsChanges()
    {
        using var rig = new EditorRig(h => h.Tools.Statuses[Tool.Uvx] = ToolStatus.NotFound);
        // "cmd /c" alone recognizes no tool; adding a second arg would flip RequiredTool to uvx
        // on a live editor (ToolRequirement unwraps one cmd /c and reads the next token).
        using var editor = rig.Editor(EditTarget.New(rig.Local("cmd", ["/c"])));
        Assert.Null(editor.RequiredTool);

        editor.Dispose();
        var raised = new List<string?>();
        editor.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        editor.Args.Add(new ArgRow("uvx"));

        Assert.Empty(raised);
        Assert.Null(editor.RequiredTool);   // RequiredTool is a cached field, never recomputed: Dispose stopped the Args.CollectionChanged relay
    }
}

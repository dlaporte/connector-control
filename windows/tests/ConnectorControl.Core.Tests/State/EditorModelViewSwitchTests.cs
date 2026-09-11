using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

public class EditorModelViewSwitchTests
{
    private const string Url = "https://scoutbook.example.com/mcp";

    [Fact]
    public void SettingIsJsonViewSwitchesToJsonAndClearsIsFormView()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.New(rig.Local("node", ["x.js"])));
        Assert.Equal(EditView.Form, editor.View);
        editor.View = EditView.Json;
        Assert.Equal(EditView.Json, editor.View);
    }

    [Fact]
    public void SettingIsFormViewFromValidJsonSwitchesBack()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.New(rig.Local("node", ["x.js"])));
        editor.RequestView(EditView.Json);
        editor.JsonText = "{\"command\": \"node\", \"args\": [\"y.js\"]}";
        editor.View = EditView.Form;
        Assert.Equal(EditView.Form, editor.View);
        Assert.Equal(["y.js"], editor.Args.Select(a => a.Value).ToArray());
    }

    /// <summary>An unparseable JSON text refuses the switch and snaps the segmented control back
    /// via PropertyChanged for View, without ever reaching the loss-warning dialog.</summary>

    [Fact]
    public void SettingIsFormViewWithUnrecoverableJsonIsRefusedAndSnapsBack()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.New(rig.Local("node", ["x.js"])));
        editor.RequestView(EditView.Json);
        editor.JsonText = "{\"command\": ";
        var raised = new List<string?>();
        editor.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        editor.View = EditView.Form;
        Assert.Equal(EditView.Json, editor.View);
        Assert.Equal(EditorModel.NotValidJson, editor.JsonError);
        Assert.Contains(nameof(EditorModel.View), raised);
        Assert.Empty(rig.H.Dialogs.Confirms);
    }

    /// <summary>Save() with unrecoverable JSON returns false and writes nothing.</summary>

    [Fact]
    public void FormToJsonSyncsTheTextAndJsonToFormAdoptsIt()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        editor.RemoteUrl = Url;
        editor.RequestView(EditView.Json);
        Assert.Equal(EditView.Json, editor.View);
        Assert.Equal("{\n  \"args\" : [\n    \"/c\",\n    \"npx\",\n    \"-y\",\n    \"mcp-remote\",\n    \"" + Url + "\"\n  ],\n  \"command\" : \"cmd\"\n}", editor.JsonText);
        Assert.Null(editor.JsonError);
        Assert.Equal(EditorModel.JsonTip, editor.JsonStatusText);

        editor.JsonText = "{\"command\": \"node\", \"args\": [\"x.js\"], \"env\": {\"K\": \"v\"}}";
        editor.RequestView(EditView.Form);
        Assert.Equal(EditView.Form, editor.View);
        Assert.False(editor.IsRemote);
        Assert.Equal("node", editor.Command);
        Assert.Equal(["x.js"], editor.Args.Select(a => a.Value).ToArray());
        Assert.False(editor.EnvRows[0].Revealed);   // re-adopted values are masked again
    }

    [Fact]
    public void FormToJsonIsBlockedByEnvValidation()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.New(rig.Local("node", ["x.js"])));
        editor.AddEnvRow();
        editor.EnvRows[0].Value = "orphan";
        editor.RequestView(EditView.Json);
        Assert.Equal(EditView.Form, editor.View);
        Assert.Equal("An environment variable value is missing its name.", editor.ValidationError);
        Assert.Equal(EditView.Form, editor.View);
    }

    [Fact]
    public void JsonToFormWithLossPromptsAndStaysUnlessForced()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.New(rig.Local("node", ["x.js"])));
        editor.RequestView(EditView.Json);
        editor.JsonText = "{\"command\": 1, \"args\": [\"a\", 2], \"env\": {\"K\": true}}";
        rig.H.Dialogs.NextConfirm = false;
        editor.RequestView(EditView.Form);
        Assert.Equal(EditView.Json, editor.View);
        var call = Assert.Single(rig.H.Dialogs.Confirms);
        Assert.Equal("Switching to Form view can’t fully represent this configuration. These elements would be lost or altered:\nargs[1] (number)\ncommand (number)\nenv.K (boolean)", call.Message);
        Assert.Equal("Switch Anyway", call.Primary);
        Assert.Equal("Stay in JSON", call.Cancel);
        Assert.True(call.Destructive);

        rig.H.Dialogs.NextConfirm = true;
        editor.RequestView(EditView.Form);
        Assert.Equal(EditView.Form, editor.View);
        Assert.Equal("", editor.Command);
        Assert.Equal(["a"], editor.Args.Select(a => a.Value).ToArray());
        Assert.Empty(editor.EnvRows);
    }

    [Fact]
    public void JsonValidationErrorDisablesSave()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.New(rig.Local("node", ["x.js"])));
        editor.RequestView(EditView.Json);
        editor.JsonText = "{\"command\": ";
        Assert.Equal("Not valid JSON — check for a stray brace, missing comma, or unquoted value.", editor.JsonError);
        Assert.Equal(editor.JsonError, editor.JsonStatusText);
        Assert.True(editor.HasJsonError);
        Assert.False(editor.CanSave);
        editor.JsonText = "{\"command\": \"node\"}";
        Assert.Null(editor.JsonError);
        Assert.True(editor.CanSave);
    }

    [Fact]
    public void AdoptingAHeaderConfigClearsTheOldBearerToken()
    {
        using var rig = new EditorRig();
        var editor = rig.Editor(EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx));
        editor.RemoteUrl = Url;
        editor.AuthKindIndex = EditorRig.AuthKindIndexOf(RemoteAuthKind.Bearer);
        editor.BearerToken = "tok";
        Assert.Equal(RemoteAuthKind.Bearer, editor.AuthKind);

        editor.RequestView(EditView.Json);
        var headerConfig = RemotePattern.Encode(new RemoteConfig(Url, new RemoteAuth.Header("X-API-Key", "v"), RemoteLaunchStyle.CmdNpx));
        editor.JsonText = headerConfig.EditorText();
        editor.RequestView(EditView.Form);

        Assert.Equal(RemoteAuthKind.Header, editor.AuthKind);
        Assert.Equal("X-API-Key", editor.HeaderName);
        Assert.Equal("", editor.BearerToken);
    }
}

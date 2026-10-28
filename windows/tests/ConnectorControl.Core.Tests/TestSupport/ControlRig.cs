using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>
/// The control layer over a real AppState in the harness's temp dir: a Settings model over fake
/// seams, a confirmer the test answers, and the tools. The integration is on (unless a test says
/// otherwise) with a bundled tool at <see cref="ToolPath"/>, so the first load has written the entry.
///
/// Mirror: Tests/ConnectorControlStateTests/TestSupport/ControlRig.swift
/// </summary>
public sealed class ControlRig : IDisposable
{
    public const string ToolPath = "/opt/connector-control/connector-control";

    public AppStateHarness H { get; }
    public AppState State { get; }
    public FakeAutostart Autostart { get; } = new();
    public FakeClaudeInstall Install { get; } = new();
    public FakeUpdater Updater { get; } = new();
    public UpdateCoordinator Updates { get; }
    public FakeControlConfirmer Confirmer { get; } = new();
    public SettingsModel SettingsModel { get; }
    public ControlTools Tools { get; }

    /// <summary>A call in flight: its answer once it has one, and every progress report so far.</summary>
    public sealed class Answer
    {
        public ControlResult? Result { get; set; }
        public List<double> Progress { get; } = [];
    }

    /// <summary><paramref name="claudeConfig"/>, when given, is what Claude's file holds before the
    /// first load, in place of the harness's realistic one.</summary>
    public ControlRig(bool integration = true, bool confirm = true, string? claudeConfig = null)
    {
        H = new AppStateHarness(seedClaudeConfig: claudeConfig is null);
        if (claudeConfig is not null)
        {
            File.WriteAllText(H.ClaudeConfigPath, claudeConfig);
        }
        H.ControlToolPath = ToolPath;
        H.Settings.ClaudeIntegrationEnabled = integration;
        H.Settings.ConfirmClaudeChanges = confirm;
        State = H.Create();
        // Matches the Mac rig turning Sparkle's fake off, so the two platforms list the same settings.
        H.Settings.AutoUpdate = false;
        Updates = new UpdateCoordinator(Updater, H.Settings, H.Notifier, H.Dialogs, AppHost.Inline());
        SettingsModel = new SettingsModel(State, H.Settings, Autostart, Install, Updater, Updates);
        Tools = new ControlTools(State, SettingsModel, Confirmer, H.Host);
        H.Drain();
    }

    /// <summary>Starts a call and runs whatever it set off, without letting any time pass. The
    /// arguments are a JSON object; none when null. The caller is Claude Desktop's relay unless
    /// given.</summary>
    public Answer Start(string name, JsonValue? arguments = null, ControlCaller? caller = null)
    {
        var answer = new Answer();
        Tools.Call(name, arguments ?? JsonValue.Object(), caller ?? new ControlCaller.Relay("claude-ai"),
            answer.Progress.Add, result => answer.Result = result);
        H.Drain();
        return answer;
    }

    /// <summary>A call's answer, or null while it is still waiting.</summary>
    public ControlResult? Call(string name, JsonValue? arguments = null, ControlCaller? caller = null) =>
        Start(name, arguments, caller).Result;

    public void Dispose()
    {
        SettingsModel.Dispose();
        Updates.Dispose();
        State.Dispose();
        H.Dispose();
    }
}

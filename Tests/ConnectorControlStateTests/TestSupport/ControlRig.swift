import Foundation
import ConnectorControlCore
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/TestSupport/ControlRig.cs
///
/// The control layer over a real AppState in the harness's temp dir: a Settings model over fake
/// seams, a confirmer the test answers, and the tools. The integration is on (unless a test says
/// otherwise) with a bundled tool at `toolPath`, so the first load has written the entry.
@MainActor
final class ControlRig {
    static let toolPath = "/opt/connector-control/connector-control"
    let h: AppStateHarness
    let state: AppState
    let autostart = FakeAutostart()
    let updater = FakeUpdater()
    let confirmer = FakeControlConfirmer()
    let settingsModel: SettingsModel
    let tools: ControlTools

    /// A call in flight: its answer once it has one, how many answers it has had (a call is
    /// answered once), and every progress report so far.
    final class Answer {
        var result: ControlResult?
        var completions = 0
        var progress: [Double] = []
    }

    /// `claudeConfig`, when given, is what Claude's file holds before the first load, in place of
    /// the harness's realistic one.
    init(integration: Bool = true, confirm: Bool = true, claudeConfig: String? = nil) {
        h = AppStateHarness(seedClaudeConfig: claudeConfig == nil)
        if let claudeConfig { try! Data(claudeConfig.utf8).write(to: h.claudeConfigURL) }
        h.controlToolPath = ControlRig.toolPath
        h.settings.claudeIntegrationEnabled = integration
        h.settings.confirmClaudeChanges = confirm
        state = h.create()
        // Sparkle's fake starts on and the Windows setting off; both start off here, so the two
        // platforms list the same settings.
        updater.automaticallyDownloadsUpdates = false
        settingsModel = SettingsModel(state: state, settings: h.settings, autostart: autostart, updater: updater)
        tools = ControlTools(state: state, settings: settingsModel, confirmer: confirmer, host: h.host)
        h.drain()
    }

    /// Starts a call and runs whatever it set off, without letting any time pass.
    @discardableResult
    func start(_ name: String, _ arguments: [String: JSONValue] = [:],
               as caller: ControlCaller = .relay(clientName: "claude-ai")) -> Answer {
        let answer = Answer()
        tools.call(name: name, arguments: .object(arguments), caller: caller,
                   progress: { answer.progress.append($0) }, completion: {
                       answer.result = $0
                       answer.completions += 1
                   })
        h.drain()
        return answer
    }

    /// A call's answer, or nil while it is still waiting.
    func call(_ name: String, _ arguments: [String: JSONValue] = [:],
              as caller: ControlCaller = .relay(clientName: "claude-ai")) -> ControlResult? {
        start(name, arguments, as: caller).result
    }

    func dispose() {
        settingsModel.dispose()
        h.dispose()
    }
}

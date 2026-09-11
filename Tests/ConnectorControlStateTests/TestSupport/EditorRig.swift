import Foundation
import ConnectorControlCore
@testable import ConnectorControlState

/// The harness + AppState + EditorModel construction every EditorModel test
/// needs. `configure` runs on the harness BEFORE its AppState is created —
/// for the handful of tests that must seed a tool status the probe reads at
/// launch.
@MainActor
final class EditorRig {
    let h = AppStateHarness()
    let state: AppState

    init(configure: (AppStateHarness) -> Void = { _ in }) {
        configure(h)
        state = h.create()
    }

    func local(_ command: String, _ args: [String],
              env: [(String, String)] = [], extra: [(String, JSONValue)] = []) -> JSONValue {
        var object: [String: JSONValue] = ["command": .string(command), "args": .array(args.map(JSONValue.string))]
        if !env.isEmpty {
            object["env"] = .object(Dictionary(uniqueKeysWithValues: env.map { ($0.0, JSONValue.string($0.1)) }))
        }
        for (key, value) in extra { object[key] = value }
        return .object(object)
    }

    func editor(_ target: EditTarget) -> EditorModel {
        EditorModel(state: state, target: target, dialogs: h.dialogs)
    }

    func dispose() {
        h.dispose()
    }
}

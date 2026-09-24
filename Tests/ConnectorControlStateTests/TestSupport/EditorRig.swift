import Foundation
import XCTest
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

    /// A copy of the active collection named `name`, left inactive: each of its connectors is an
    /// identical twin of the active collection's.
    func twin(_ name: String, file: StaticString = #filePath, line: UInt = #line) {
        let active = state.activeCollection
        XCTAssertNil(state.createCollection(named: name), file: file, line: line)
        state.switchCollection(to: active)
    }

    func editor(_ target: EditTarget) -> EditorModel {
        EditorModel(state: state, target: target, dialogs: h.dialogs)
    }

    /// An editor on a named collection's copy of a connector, read out of the store the way the
    /// Collections window's row will. A name that collection does not hold is a test bug, so it
    /// trips the force-unwrap rather than quietly opening an empty window.
    func editor(_ name: String, in collection: String) -> EditorModel {
        editor(EditTarget.existing(name: name, entry: state.store.collections[collection]!.mcps[name]!,
                                   in: collection))
    }

    func dispose() {
        h.dispose()
    }
}

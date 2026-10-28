import Foundation
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/TestSupport/ControlTranscript.cs
///
/// Replays one shared recording under `Tests/Fixtures/control/` against a fresh rig seeded from
/// `claude_config.json`, through a real `McpSession` and `ControlToolHost`. This side alone may
/// rewrite a recording's `< ` lines, when CONNECTOR_CONTROL_UPDATE_GOLDENS=1; the Windows mirror
/// only ever compares, byte for byte.
///
/// A recording is UTF-8 with `\n` line ends. `# ` lines are comments; `#! <key> <value>` lines set
/// up the rig (`integration on|off`, `confirm on|off`, `answer allow|deny`, `caller relay|cli`,
/// `backup on`); `> ` lines go to the session in order, and each line the session sends back is a
/// `< ` line after the request that drew it. The master list's path, the one temp path a result
/// carries, is `<master-list>` in a recording.
@MainActor
enum ControlTranscript {
    static let masterListToken = "<master-list>"

    struct Replay {
        let rig: ControlRig
        let session: McpSession
        let expected: [String]
        let actual: [String]
    }

    /// The Windows mirror names this `Run`: C# cannot name a method after a nested type of its class.
    static func replay(_ name: String) throws -> Replay {
        let url = Fixtures.url("control/\(name).transcript")
        let lines = try String(contentsOf: url, encoding: .utf8).components(separatedBy: "\n").filter { !$0.isEmpty }
        var settings: [String: String] = [:]
        for line in lines where line.hasPrefix("#! ") {
            let parts = line.dropFirst(3).split(separator: " ", maxSplits: 1).map(String.init)
            if parts.count == 2 { settings[parts[0]] = parts[1] }
        }
        let rig = ControlRig(integration: settings["integration"] != "off", confirm: settings["confirm"] != "off",
                             claudeConfig: Fixtures.text("control/claude_config.json"))
        switch settings["answer"] {
        case "allow": rig.confirmer.scripted = true
        case "deny": rig.confirmer.scripted = false
        default: rig.confirmer.scripted = nil
        }
        if settings["backup"] == "on" {
            _ = try rig.state.service.backups.backUp(fileAt: rig.h.claudeConfigURL, series: RestoreModel.series, now: rig.h.now)
        }
        let host = ControlToolHost(tools: rig.tools, isCLI: settings["caller"] == "cli")
        var sent: [String] = []
        let session = McpSession(host: host, version: host.version, send: { sent.append($0) })
        host.session = session
        // The path as a JSON string spells it, without its quotes: how it appears inside any line.
        let masterList = JSONValue.string(rig.state.service.paths.masterStoreURL.path).compactLine()
        let escaped = String(masterList.dropFirst().dropLast())
        var expected: [String] = []
        var actual: [String] = []
        var rewritten: [String] = []
        for line in lines {
            if line.hasPrefix("< ") {
                expected.append(String(line.dropFirst(2)))
                continue
            }
            rewritten.append(line)
            guard line.hasPrefix("> ") else { continue }
            sent.removeAll()
            session.receive(String(line.dropFirst(2)))
            rig.h.drain()
            // Literal, as the Windows mirror's ordinal Replace is: Foundation would otherwise match
            // a path spelled with other but canonically equivalent characters.
            let produced = sent.map { $0.replacingOccurrences(of: escaped, with: masterListToken, options: .literal) }
            actual += produced
            rewritten += produced.map { "< " + $0 }
        }
        if ProcessInfo.processInfo.environment["CONNECTOR_CONTROL_UPDATE_GOLDENS"] == "1" {
            try (rewritten.joined(separator: "\n") + "\n").write(to: url, atomically: true, encoding: .utf8)
            expected = actual
        }
        return Replay(rig: rig, session: session, expected: expected, actual: actual)
    }
}

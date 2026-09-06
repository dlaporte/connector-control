import Foundation

/// Fixtures shared with the Core tests and the Windows suite live in `Tests/Fixtures/`.
enum Fixtures {
    static func url(_ name: String) -> URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()   // Tests/ConnectorControlStateTests/TestSupport
            .deletingLastPathComponent()   // Tests/ConnectorControlStateTests
            .deletingLastPathComponent()   // Tests
            .appendingPathComponent("Fixtures")
            .appendingPathComponent(name)
    }

    static func text(_ name: String) -> String {
        // A missing fixture is a test-suite bug; crash loudly.
        try! String(contentsOf: url(name), encoding: .utf8)
    }

    static var realisticClaudeConfig: String { text("realistic_claude_config.json") }
}

import Foundation

/// Fixtures shared with the Windows test suite live in `Tests/Fixtures/`.
enum Fixtures {
    static func url(_ name: String) -> URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()   // Tests/ConnectorControlCoreTests
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

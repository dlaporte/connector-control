import Foundation

/// Fixtures shared with the Windows test suite live in `Tests/Fixtures/`.
public enum Fixtures {
    public static func url(_ name: String) -> URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()   // Tests/ConnectorControlTestSupport
            .deletingLastPathComponent()   // Tests
            .appendingPathComponent("Fixtures")
            .appendingPathComponent(name)
    }

    public static func text(_ name: String) -> String {
        // A missing fixture is a test-suite bug; crash loudly.
        try! String(contentsOf: url(name), encoding: .utf8)
    }

    public static var realisticClaudeConfig: String { text("realistic_claude_config.json") }
}

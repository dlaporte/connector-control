import Foundation

/// Claude Desktop's expected code identity: the bundle ID, the Developer ID
/// team every shipped Claude.app is signed by, the compiled code-signing
/// requirement a bundle must satisfy before this app will quit and relaunch
/// it, and the user-facing messages `ClaudeRestarter` shows when a bundle
/// fails that check. Kept here (not on `ClaudeRestarter`) so the strings can
/// be pinned by a State-layer test without pulling in AppKit/Security.
public enum ClaudeSignature {
    public static let bundleID = "com.anthropic.claudefordesktop"
    /// Anthropic PBC's Developer ID team, as on every shipped Claude.app.
    public static let teamIdentifier = "Q6L2SF6YDW"
    /// Claude Desktop, signed by Anthropic — under either a Developer ID or an
    /// App Store certificate chained to Apple. The path this app launches is a
    /// plain string in UserDefaults, so before it quits Claude and starts
    /// whatever sits at that path, the bundle has to prove it is Claude.
    public static let requirement =
        "anchor apple generic and identifier \"\(bundleID)\" and certificate leaf[subject.OU] = \"\(teamIdentifier)\""

    public static func notFoundMessage(path: String) -> String {
        "Claude Desktop was not found at \(path)."
    }

    public static func refusalMessage(name: String, detail: String) -> String {
        "\(name) is not Claude Desktop signed by Anthropic (\(detail)). " + AppState.chooseClaude
    }

    public static func uninspectableMessage(name: String) -> String {
        "\(name) is not an app bundle this app can inspect."
    }

    public static let requirementCompileFailure = "The Claude Desktop signing requirement could not be compiled."
}

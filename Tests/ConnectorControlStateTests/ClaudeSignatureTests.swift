import XCTest
@testable import ConnectorControlState

/// These strings used to live on `ClaudeRestarter` (Sources/ConnectorControl,
/// AppKit/Security); moved here byte-for-byte so they can be pinned without
/// pulling in either.
final class ClaudeSignatureTests: XCTestCase {
    func testIdentityConstants() {
        XCTAssertEqual(ClaudeSignature.bundleID, "com.anthropic.claudefordesktop")
        XCTAssertEqual(ClaudeSignature.teamIdentifier, "Q6L2SF6YDW")
        XCTAssertEqual(ClaudeSignature.requirement,
                       "anchor apple generic and identifier \"com.anthropic.claudefordesktop\" "
                       + "and certificate leaf[subject.OU] = \"Q6L2SF6YDW\"")
    }

    func testMessages() {
        XCTAssertEqual(ClaudeSignature.notFoundMessage(path: "/Applications/Claude.app"),
                       "Claude Desktop was not found at /Applications/Claude.app.")
        XCTAssertEqual(ClaudeSignature.refusalMessage(name: "Fake.app", detail: "unsigned"),
                       "Fake.app is not Claude Desktop signed by Anthropic (unsigned). "
                       + AppState.chooseClaude)
        XCTAssertEqual(ClaudeSignature.uninspectableMessage(name: "Fake.app"),
                       "Fake.app is not an app bundle this app can inspect.")
        XCTAssertEqual(ClaudeSignature.requirementCompileFailure,
                       "The Claude Desktop signing requirement could not be compiled.")
    }
}

import XCTest
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/CredentialHeuristicsTests.cs
final class CredentialHeuristicsTests: XCTestCase {
    func testKnownPrefixesAndLongMixedTokens() {
        XCTAssertTrue(CredentialHeuristics.looksLikeCredential("sk-live-9f3a"))
        XCTAssertTrue(CredentialHeuristics.looksLikeCredential("ghp_abc123"))
        XCTAssertTrue(CredentialHeuristics.looksLikeCredential("xoxb-1-2"))
        XCTAssertTrue(CredentialHeuristics.looksLikeCredential("Bearer abc"))
        XCTAssertTrue(CredentialHeuristics.looksLikeCredential("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6"))
        XCTAssertTrue(CredentialHeuristics.looksLikeCredential("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4"), "32 characters is the boundary")
        XCTAssertFalse(CredentialHeuristics.looksLikeCredential("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d"), "31 characters is below it")
    }
    func testOrdinaryArgumentsAreNotFlagged() {
        XCTAssertFalse(CredentialHeuristics.looksLikeCredential("-y"))
        XCTAssertFalse(CredentialHeuristics.looksLikeCredential("@dbt/mcp"))
        XCTAssertFalse(CredentialHeuristics.looksLikeCredential("https://example.com/mcp"))
        XCTAssertFalse(CredentialHeuristics.looksLikeCredential("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"))
        XCTAssertFalse(CredentialHeuristics.looksLikeCredential("${CC_NEEDS:token}"))
        // A digit is a decimal digit: a superscript or a vulgar fraction is not one.
        XCTAssertFalse(CredentialHeuristics.looksLikeCredential("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\u{B2}\u{BD}"))
    }
}

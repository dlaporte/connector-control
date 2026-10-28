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

    /// The same text reads the same on both platforms: Windows compares, counts and lowers UTF-16
    /// units, so a combining mark must not hide a name or a prefix here, and a character beyond the
    /// Basic Plane counts as its two units, neither a letter nor a digit.
    func testTextIsReadUnitByUnitAsOnWindows() {
        XCTAssertTrue(CredentialHeuristics.namesASecret("--api-key\u{301}"))
        XCTAssertTrue(CredentialHeuristics.namesASecret("API_TOKEN\u{301}"))
        XCTAssertTrue(CredentialHeuristics.namesASecret("X-API-\u{212A}EY"), "the Kelvin sign lowers to k")
        XCTAssertTrue(CredentialHeuristics.looksLikeCredential("sk-\u{301}live"))
        XCTAssertFalse(CredentialHeuristics.looksLikeCredential("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4 \u{301}x"), "a space is a space")
        XCTAssertTrue(CredentialHeuristics.looksLikeCredential("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3\u{1F600}"), "32 units")
        XCTAssertFalse(CredentialHeuristics.looksLikeCredential(String(repeating: "1", count: 31) + "\u{1D538}"),
                       "a letter beyond the Basic Plane is two surrogate units, neither a letter")
        XCTAssertTrue(CredentialHeuristics.looksLikeRandomToken("AbcdefghijKLMNOPQ1\u{1F600}"), "20 units")
    }
}

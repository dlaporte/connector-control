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
        XCTAssertFalse(CredentialHeuristics.namesASecret("CREDENT\u{130}AL"),
                       "İ lowers to two scalars, i and a combining dot, and stays as it is, as ToLowerInvariant leaves it")
        XCTAssertEqual(CredentialHeuristics.loweredUnits("\u{130}I"), [0x130, 0x69])
        XCTAssertTrue(CredentialHeuristics.looksLikeCredential("sk-\u{301}live"))
        XCTAssertFalse(CredentialHeuristics.looksLikeCredential("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4 \u{301}x"), "a space is a space")
        XCTAssertTrue(CredentialHeuristics.looksLikeCredential("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3\u{1F600}"), "32 units")
        XCTAssertFalse(CredentialHeuristics.looksLikeCredential(String(repeating: "1", count: 31) + "\u{1D538}"),
                       "a letter beyond the Basic Plane is two surrogate units, neither a letter")
        XCTAssertTrue(CredentialHeuristics.looksLikeRandomToken("AbcdefghijKLMNOPQ1\u{1F600}"), "20 units")
    }

    /// Variables whose value names code to run, whatever their case.
    func testNamesAProgramToRun() {
        for name in ["ASKPASS", "GIT_ASKPASS_HELPER", "my_askpass_tool", "CARGO_REGISTRY_CREDENTIAL_PROVIDER",
                     "CARGO_REGISTRIES_MY_REG_CREDENTIAL_PROVIDER", "NUGET_CREDENTIAL_PROVIDER", "GIT_ASKPASS", "SSH_ASKPASS", "SSH_ASKPASS_REQUIRE", "SUDO_ASKPASS", "MY_TOOL_ASKPASS", "git_askpass",
                     "GIT_SSH", "GIT_SSH_COMMAND", "GIT_EXEC_PATH", "GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0",
                     "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "NODE_OPTIONS", "Node_Options", "LD_PRELOAD", "DYLD_INSERT_LIBRARIES",
                     "PYTHONSTARTUP", "BASH_ENV", "ENV", "env", "PERL5OPT", "RUBYOPT", "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS"] {
            XCTAssertTrue(CredentialHeuristics.namesAProgramToRun(name), name)
        }
        for name in ["GITHUB_TOKEN", "API_KEY", "PASSWORD", "GIT_CONFIG", "ENVIRONMENT", "MY_ENV", "PATH", "NODE_ENV",
                     "LD_PRELOAD_X", "GIT_SSH_X", "CREDENTIAL_PROVIDER_HINT", "ASKPAS"] {
            XCTAssertFalse(CredentialHeuristics.namesAProgramToRun(name), name)
        }
    }

    func testSchemeWords() {
        for word in ["Bearer", "bearer", "BASIC", "Token"] { XCTAssertTrue(CredentialHeuristics.isSchemeWord(word), word) }
        for word in ["Bearer ", "Digest", "tokens", "", "Bear"] { XCTAssertFalse(CredentialHeuristics.isSchemeWord(word), word) }
    }

    /// Beside its references a value may hold one word of letters, a scheme (`Bearer`, `SSWS`,
    /// `Bot`), before them: text after a reference can be the secret itself.
    func testAReferenceTakesOnlyOneWordBeforeIt() {
        for value in ["${API_TOKEN}", "$API_TOKEN", "Bearer ${API_TOKEN}", "basic $CRED", "Token ${CC_NEEDS:T}", "${A} ${B}", "Bearer${A}",
                      "SSWS ${T}", "Bot ${T}", "ApiKey ${T}", "Digest ${A}"] {
            XCTAssertTrue(CredentialHeuristics.isReference(value), value)
        }
        for value in ["${CC_NEEDS:PW}hunter", "${A} hunter", "${A} Bearer", "Bearer ${A} extra", "Bearer", "two words ${A}", "hunter2 ${A}"] {
            XCTAssertFalse(CredentialHeuristics.isReference(value), value)
        }
    }
}

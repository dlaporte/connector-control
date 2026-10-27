import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ControlMaskingTests.cs
@MainActor
final class ControlMaskingTests: XCTestCase {
    private let token = "ghp_0123456789abcdefghijklmnopqrstuvwxyzAB"

    private var github: JSONValue {
        .object([
            "command": .string("npx"),
            "args": .array([.string("-y"), .string("@modelcontextprotocol/server-github")]),
            "env": .object(["GITHUB_TOKEN": .string(token), "EMPTY": .string(""), "LATER": .string("${CC_NEEDS:LATER}")]),
        ])
    }

    private func splunk(url: String, token: String) -> JSONValue {
        RemotePattern.encode(RemoteConfig(url: url, auth: .bearer(token: token)))
    }

    func testEnvironmentValuesReadAsSetOrNeedsValue() {
        XCTAssertEqual(ControlMasking.maskedConfig(github), .object([
            "command": .string("npx"),
            "args": .array([.string("-y"), .string("@modelcontextprotocol/server-github")]),
            "env": .object(["GITHUB_TOKEN": .string("set"), "EMPTY": .string("needs value"), "LATER": .string("needs value")]),
        ]))
    }

    func testArgumentsThatReadAsCredentialsAreMasked() {
        XCTAssertEqual(ControlMasking.arguments([
            "--token", "abc", "--api-key=sk-123", token, "/Users/x/server.js", "${CC_NEEDS:server_path}",
            "${COLLECTION_DIR}/srv.js", "--port", "8080",
        ]), [
            "--token", "•••", "--api-key=•••", "•••", "/Users/x/server.js", "${CC_NEEDS:server_path}",
            "${COLLECTION_DIR}/srv.js", "--port", "8080",
        ])
    }

    func testAURLKeepsItsHostAndPathAndMasksItsPasswordAndQueryValues() {
        XCTAssertEqual(ControlMasking.url("https://user:hunter2@example.com/mcp?api_key=abc&region=us#top"),
                       "https://user:•••@example.com/mcp?api_key=•••&region=•••#top")
        XCTAssertEqual(ControlMasking.url("https://example.com/mcp?token=${CC_NEEDS:token}"),
                       "https://example.com/mcp?token=${CC_NEEDS:token}", "a placeholder is shown as written")
        XCTAssertEqual(ControlMasking.url("https://example.com/mcp"), "https://example.com/mcp")
        XCTAssertEqual(ControlMasking.url("not a url"), "not a url")
    }

    func testAHeadersValueIsMaskedUnlessItRefersElsewhere() {
        XCTAssertEqual(ControlMasking.arguments(["-y", "mcp-remote", "https://x.example/mcp?k=v",
                                                 "--header", "Authorization:${AUTH_HEADER}", "--header", "X-Team: blue"]),
                       ["-y", "mcp-remote", "https://x.example/mcp?k=•••",
                        "--header", "Authorization:${AUTH_HEADER}", "--header", "X-Team: •••"])
    }

    func testTheOAuthClientInfoAfterItsFlagIsMasked() {
        XCTAssertEqual(ControlMasking.arguments(["--static-oauth-client-info", "{\"client_id\":\"a\",\"client_secret\":\"b\"}"]),
                       ["--static-oauth-client-info", "•••"])
    }

    func testCodeLinesForALocalConnector() {
        XCTAssertEqual(ControlMasking.codeLines(github), [
            "command   npx",
            "args      -y @modelcontextprotocol/server-github",
            "env       EMPTY = needs your value",
            "          GITHUB_TOKEN = ••• (set)",
            "          LATER = needs your value",
        ])
    }

    func testCodeLinesForARemoteConnectorNameItsBridgeURLAndHeader() {
        XCTAssertEqual(ControlMasking.codeLines(splunk(url: "https://harvard.splunkcloud.com/mcp?tenant=acme", token: "s3cr3t")), [
            "type      remote, through mcp-remote",
            "url       https://harvard.splunkcloud.com/mcp?tenant=•••",
            "headers   Authorization = ••• (set)",
        ])
    }

    func testRevealShowsEverythingAsWritten() {
        XCTAssertEqual(ControlMasking.maskedConfig(github, reveal: true), github)
        XCTAssertTrue(ControlMasking.codeLines(github, reveal: true).contains("          GITHUB_TOKEN = \(token)"))
        XCTAssertEqual(ControlMasking.url("https://u:p@h.example/?a=b", reveal: true), "https://u:p@h.example/?a=b")
    }

    func testChangeLinesShowOnlyWhatDiffersAndAChangedSecretAsChanged() {
        let before = splunk(url: "https://a.example/mcp", token: "one")
        let after = splunk(url: "https://b.example/mcp", token: "two")
        XCTAssertEqual(ControlMasking.changeLines(from: before, to: after), [
            "url",
            "  − https://a.example/mcp",
            "  + https://b.example/mcp",
            "headers",
            "  − Authorization = ••• (set)",
            "  + Authorization = ••• (set)",
        ])
        XCTAssertEqual(ControlMasking.changeLines(from: before, to: before), [])
    }

    func testNoSecretSurvivesMasking() throws {
        for config in [github, splunk(url: "https://h.example/mcp?key=k3y", token: "s3cr3t")] {
            let text = try XCTUnwrap(String(data: try ControlMasking.maskedConfig(config).serialized(), encoding: .utf8))
                + ControlMasking.codeLines(config).joined()
            for secret in [token, "s3cr3t", "k3y"] { XCTAssertFalse(text.contains(secret), secret) }
        }
    }

    func testNoSecretSurvivesInArgumentsURLsOrChanges() {
        let shown = (ControlMasking.arguments(["--token", "abc9", "--api-key=sk-123x", token, "--header", "X-Team: blue7"])
            + [ControlMasking.url("https://user:hunter2@example.com/mcp?api_key=abc9&region=us7#top")]
            + ControlMasking.changeLines(from: splunk(url: "https://a.example/mcp?k=old1", token: "one1"),
                                         to: splunk(url: "https://a.example/mcp?k=new2", token: "two2"))).joined(separator: "\n")
        for secret in [token, "abc9", "sk-123x", "blue7", "hunter2", "us7", "old1", "new2", "one1", "two2"] {
            XCTAssertFalse(shown.contains(secret), secret)
        }
    }
}

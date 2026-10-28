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

    /// Every masked view of `config` — its structured form, its code lines and its change lines
    /// from nothing — without any of `secrets`.
    private func assertHidden(_ secrets: [String], in config: JSONValue,
                              file: StaticString = #filePath, line: UInt = #line) throws {
        let structured = try XCTUnwrap(String(data: try ControlMasking.maskedConfig(config).serialized(), encoding: .utf8))
        let text = (ControlMasking.codeLines(config) + ControlMasking.changeLines(from: .object([:]), to: config))
            .joined(separator: "\n")
        for secret in secrets {
            XCTAssertFalse(structured.contains(secret), "structured: \(secret)", file: file, line: line)
            XCTAssertFalse(text.contains(secret), "text: \(secret)", file: file, line: line)
        }
    }

    private func local(_ command: String, _ args: [String]) -> JSONValue {
        .object(["command": .string(command), "args": .array(args.map(JSONValue.string))])
    }

    func testAPlaceholderInsideAnArgumentLeavesTheRestMasked() throws {
        let args = ["-y", "mcp-remote", "https://h.example/mcp?api_key=sk-live-1234&user=${CC_NEEDS:user}"]
        XCTAssertEqual(ControlMasking.arguments(args),
                       ["-y", "mcp-remote", "https://h.example/mcp?api_key=•••&user=${CC_NEEDS:user}"])
        XCTAssertEqual(ControlMasking.argument("https://user:hunter2@h.example/${CC_NEEDS:path}"),
                       "https://user:•••@h.example/${CC_NEEDS:path}")
        XCTAssertEqual(ControlMasking.argument("--api-key=sk-123${CC_NEEDS:rest}"), "--api-key=•••")
        XCTAssertEqual(ControlMasking.argument("sk-live-1234${CC_NEEDS:rest}"), "•••")
        XCTAssertEqual(ControlMasking.argument("${CC_NEEDS:server_path}"), "${CC_NEEDS:server_path}")
        try assertHidden(["sk-live-1234"], in: local("npx", args))
        try assertHidden(["hunter2"], in: local("node", ["s.js", "https://user:hunter2@h.example/${CC_NEEDS:path}"]))
        try assertHidden(["sk-123", "sk-live-1234"],
                         in: local("node", ["--api-key=sk-123${CC_NEEDS:rest}", "sk-live-1234${CC_NEEDS:rest}"]))
    }

    func testAURLMasksACredentialInItsUserNamePathOrFragment() throws {
        let zapier = "https://actions.zapier.com/mcp/sk-ak-ZjQ2YTk1NzQtYmM0Ny00/sse"
        let user = "https://ghp_0123456789abcdefghijklmnopqrstuvwxAB@api.example.com/mcp"
        let basic = "https://ghp_0123456789abcdefghijklmnopqrstuvwxAB:x-oauth-basic@api.example.com/mcp"
        let fragment = "https://h.example/cb#access_token=hunter2"
        XCTAssertEqual(ControlMasking.url(zapier), "https://actions.zapier.com/mcp/•••/sse")
        XCTAssertEqual(ControlMasking.url(user), "https://•••@api.example.com/mcp")
        XCTAssertEqual(ControlMasking.url(basic), "https://•••:•••@api.example.com/mcp")
        XCTAssertEqual(ControlMasking.url(fragment), "https://h.example/cb#access_token=•••")
        XCTAssertEqual(ControlMasking.url("https://bob@h.example/a/b/#top"), "https://bob@h.example/a/b/#top",
                       "a plain user name, path and fragment show as written")
        try assertHidden(["sk-ak-ZjQ2YTk1NzQtYmM0Ny00"], in: .object(["url": .string(zapier)]))
        try assertHidden(["ghp_0123456789abcdefghijklmnopqrstuvwxAB", "hunter2"], in: local("node", ["s.js", user, basic, fragment]))
    }

    func testANameEqualsValueWordNamedForASecretIsMasked() throws {
        let docker = ["run", "-i", "--rm", "-e", "DB_PASSWORD=hunter2", "-e", "API_KEY=abc-123", "img"]
        XCTAssertEqual(ControlMasking.arguments(docker),
                       ["run", "-i", "--rm", "-e", "DB_PASSWORD=•••", "-e", "API_KEY=•••", "img"])
        XCTAssertEqual(ControlMasking.arguments(["API_KEY=hunter2", "node", "s.js"]), ["API_KEY=•••", "node", "s.js"])
        XCTAssertEqual(ControlMasking.argument("apikeyQ2xpZW50U2VjcmV0MTIzNDU2Nzg5MA=="), "•••",
                       "a token padded with = is no name: the whole word stays masked")
        try assertHidden(["hunter2", "abc-123"], in: local("docker", docker))
        try assertHidden(["hunter2"], in: local("env", ["API_KEY=hunter2", "node", "s.js"]))
    }

    func testAValueUnderAKeyNamedForASecretIsMasked() throws {
        let oauth: JSONValue = .object([
            "url": .string("https://h.example/mcp"),
            "oauth": .object(["clientId": .string("a"), "clientSecret": .string("hunter2")]),
        ])
        XCTAssertEqual(ControlMasking.maskedConfig(oauth), .object([
            "url": .string("https://h.example/mcp"),
            "oauth": .object(["clientId": .string("set"), "clientSecret": .string("set")]),
        ]), "everything under a key named for a secret is masked, as `oauth` is")
        let key: JSONValue = .object(["command": .string("node"), "args": .array([.string("s.js")]), "apiKey": .string("hunter2")])
        XCTAssertEqual(ControlMasking.maskedConfig(key),
                       .object(["command": .string("node"), "args": .array([.string("s.js")]), "apiKey": .string("set")]))
        try assertHidden(["hunter2"], in: oauth)
        try assertHidden(["hunter2"], in: key)
        try assertHidden(["hunter2"], in: .object(["command": .string("node"), "args": .array([.string("--token"), .string("hunter2"), .int(1)])]))
    }

    func testAFlagAfterASecretNamedFlagIsNotTakenForItsValue() {
        XCTAssertEqual(ControlMasking.arguments(["--no-auth", "--allow-http", "--bypass-sandbox", "--yolo", "--token", "abc"]),
                       ["--no-auth", "--allow-http", "--bypass-sandbox", "--yolo", "--token", "•••"])
    }

    /// The Windows mirror cuts at a delimiter by UTF-16 unit; a combining mark after the delimiter
    /// must not move the cut, or the two platforms show the same text differently.
    func testACombiningMarkAfterADelimiterCutsTheSameOnBothPlatforms() {
        XCTAssertEqual(ControlMasking.url("https://u:hunter2@\u{301}h.example/"), "https://u:•••@\u{301}h.example/")
        XCTAssertEqual(ControlMasking.url("https://h.example/?a=b&\u{301}c=hunter2"), "https://h.example/?a=•••&\u{301}c=•••")
        XCTAssertEqual(ControlMasking.argument("--api-key=\u{301}hunter2"), "--api-key=•••")
        XCTAssertEqual(ControlMasking.argument("-\u{301}-api-key=hunter2"), "-\u{301}-api-key=•••")
        XCTAssertEqual(ControlMasking.arguments(["--header", "X-Key:\u{301}hunter2"]), ["--header", "X-Key: •••"])
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

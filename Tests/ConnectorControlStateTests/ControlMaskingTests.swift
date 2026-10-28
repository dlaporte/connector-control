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

    private var notion: JSONValue {
        .object([
            "url": .string("https://mcp.notion.com/mcp?x=1"),
            "headers": .object(["Authorization": .string("Bearer t0k"), "X-Empty": .string("")]),
        ])
    }

    /// An mcp-remote config for https://h.example/mcp, written by hand: `bridge` before the URL,
    /// `args` after it.
    private func remote(_ args: [String], env: [String: JSONValue] = [:], bridge: [String] = ["-y", "mcp-remote"]) -> JSONValue {
        var object: [String: JSONValue] = [
            "command": .string("npx"),
            "args": .array((bridge + ["https://h.example/mcp"] + args).map(JSONValue.string)),
        ]
        if !env.isEmpty { object["env"] = .object(env) }
        return .object(object)
    }

    private func local(_ command: String, _ args: [String]) -> JSONValue {
        .object(["command": .string(command), "args": .array(args.map(JSONValue.string))])
    }

    private func adding(_ key: String, _ value: JSONValue, to config: JSONValue) -> JSONValue {
        guard case .object(var object) = config else { return config }
        object[key] = value
        return .object(object)
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

    func testCodeLinesForAConnectorReachedByURLAlone() {
        XCTAssertEqual(ControlMasking.codeLines(notion), [
            "type      remote",
            "url       https://mcp.notion.com/mcp?x=•••",
            "headers   Authorization = ••• (set)",
            "          X-Empty = needs your value",
        ])
    }

    func testCodeLinesForAnOAuthClientShowItsIDButNotItsSecret() {
        let config = RemotePattern.encode(RemoteConfig(url: "https://h.example/mcp",
                                                       auth: .oauthClient(clientID: "cid", clientSecret: "csec", scopes: "read write")))
        XCTAssertEqual(ControlMasking.codeLines(config), [
            "type     remote, through mcp-remote",
            "url      https://h.example/mcp",
            "oauth    client cid, secret ••• (set)",
            "scopes   read write",
        ])
        XCTAssertTrue(ControlMasking.codeLines(config, exposure: .revealed).contains("oauth    client cid, secret csec"))
    }

    func testAHeaderAfterDashHIsMaskedAsAfterHeader() {
        XCTAssertEqual(ControlMasking.arguments(["-H", "X-Api-Key: abc", "-H", "Authorization:${AUTH}"]),
                       ["-H", "X-Api-Key: •••", "-H", "Authorization:${AUTH}"])
    }

    func testRevealShowsEverythingAsWritten() {
        XCTAssertEqual(ControlMasking.maskedConfig(github, reveal: true), github)
        XCTAssertTrue(ControlMasking.codeLines(github, exposure: .revealed).contains("          GITHUB_TOKEN = \(token)"))
        XCTAssertEqual(ControlMasking.url("https://u:p@h.example/?a=b", reveal: true), "https://u:p@h.example/?a=b")
        XCTAssertEqual(ControlMasking.arguments(["--token", "abc", token, "--header", "X-Key: v"], reveal: true),
                       ["--token", "abc", token, "--header", "X-Key: v"])
    }

    /// The confirmation dialog, which nothing sends back to Claude, shows what would run in full:
    /// only environment and header values, the OAuth client secret and a key named for a secret
    /// stay masked.
    func testTheDialogShowsTheCommandArgumentsAndURLInFull() {
        let docker: JSONValue = .object([
            "command": .string("docker"),
            "args": .array(["run", "-e", "API_KEY=hunter2", "--token", "abc", "https://u:pw@h.example/x?k=v",
                            "--header", "X-Key: hval", "--static-oauth-client-info", #"{"client_id":"a","client_secret":"csec"}"#,
                            "img"].map(JSONValue.string)),
            "env": .object(["GITHUB_TOKEN": .string(token)]),
            "apiKey": .string("topsecret"),
        ])
        XCTAssertEqual(ControlMasking.codeLines(docker, exposure: .dialog), [
            "command   docker",
            "args      run -e API_KEY=hunter2 --token abc https://u:pw@h.example/x?k=v --header X-Key: ••• --static-oauth-client-info ••• img",
            "env       GITHUB_TOKEN = ••• (set)",
            "other     apiKey",
        ])
        let oauth = RemotePattern.encode(RemoteConfig(url: "https://h.example/mcp?tenant=acme",
                                                      auth: .oauthClient(clientID: "cid", clientSecret: "csec", scopes: "read")))
        XCTAssertEqual(ControlMasking.codeLines(oauth, exposure: .dialog), [
            "type     remote, through mcp-remote",
            "url      https://h.example/mcp?tenant=acme",
            "oauth    client cid, secret ••• (set)",
            "scopes   read",
        ])
        XCTAssertEqual(ControlMasking.codeLines(notion, exposure: .dialog), [
            "type      remote",
            "url       https://mcp.notion.com/mcp?x=1",
            "headers   Authorization = ••• (set)",
            "          X-Empty = needs your value",
        ])
        let shown = [docker, oauth, notion].flatMap { config in
            ControlMasking.codeLines(config, exposure: .dialog)
                + ControlMasking.changeLines(from: .object([:]), to: config, exposure: .dialog)
        }.joined(separator: "\n")
        for secret in [token, "hval", "csec", "topsecret", "t0k"] { XCTAssertFalse(shown.contains(secret), secret) }
        XCTAssertEqual(ControlMasking.changeLines(from: local("node", []), to: adding("apiKey", .string("topsecret"), to: local("node", [])),
                                                  exposure: .dialog), ["apiKey", "  + ••• (set)"])
    }

    func testANonStringEnvironmentOrHeaderValueReadsAsSet() {
        let node: JSONValue = .object(["command": .string("node"), "env": .object(["PORT": .int(8080), "TOKEN": .string("hunter2")])])
        XCTAssertEqual(ControlMasking.codeLines(node), ["command   node", "env       PORT = ••• (set)", "          TOKEN = ••• (set)"])
        XCTAssertEqual(ControlMasking.maskedConfig(node),
                       .object(["command": .string("node"), "env": .object(["PORT": .string("set"), "TOKEN": .string("set")])]))
        let native: JSONValue = .object(["url": .string("https://h.example/mcp"), "headers": .object(["X-Retries": .int(3)])])
        XCTAssertEqual(ControlMasking.codeLines(native), ["type      remote", "url       https://h.example/mcp", "headers   X-Retries = ••• (set)"])
        XCTAssertEqual(ControlMasking.codeLines(native, exposure: .revealed).last, "headers   X-Retries = 3")
        let bridged = remote(["--header", "Authorization:${AUTH_HEADER}"],
                             env: ["AUTH_HEADER": .string("Bearer s3cr3t"), "RETRIES": .int(3)])
        XCTAssertEqual(ControlMasking.codeLines(bridged), [
            "type      remote, through mcp-remote",
            "url       https://h.example/mcp",
            "headers   Authorization = ••• (set)",
            "env       RETRIES = ••• (set)",
        ])
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

    func testChangeLinesForALocalConnector() {
        let after: JSONValue = .object([
            "command": .string("npx"),
            "args": .array([.string("-y"), .string("@modelcontextprotocol/server-github"), .string("--read-only")]),
            "env": .object(["GITHUB_TOKEN": .string(token + "x"), "EMPTY": .string("x"), "LATER": .string("${CC_NEEDS:LATER}")]),
        ])
        XCTAssertEqual(ControlMasking.changeLines(from: github, to: after), [
            "args",
            "  − -y @modelcontextprotocol/server-github",
            "  + -y @modelcontextprotocol/server-github --read-only",
            "env",
            "  − EMPTY = needs your value",
            "  − GITHUB_TOKEN = ••• (set)",
            "  + EMPTY = ••• (set)",
            "  + GITHUB_TOKEN = ••• (set)",
        ])
    }

    /// Every difference between two configs shows as at least one change, whatever the view: a
    /// confirmation that showed none would let a change through unseen.
    func testEveryDifferenceShowsAsAChange() {
        let info = #"{"client_id":"a","client_secret":"s"}"#
        let edits: [(String, JSONValue, JSONValue)] = [
            ("another key", adding("cwd", .string("/a"), to: local("node", ["s.js"])), adding("cwd", .string("/b"), to: local("node", ["s.js"]))),
            ("type", adding("type", .string("sse"), to: notion), adding("type", .string("http"), to: notion)),
            ("unparseable client info", remote(["--static-oauth-client-info", "not json one"]),
             remote(["--static-oauth-client-info", "not json two"])),
            ("client metadata", remote(["--static-oauth-client-info", info, "--static-oauth-client-metadata", #"{"redirect_uri":"https://a.example/cb","scope":"read"}"#]),
             remote(["--static-oauth-client-info", info, "--static-oauth-client-metadata", #"{"redirect_uri":"https://evil.example/cb","scope":"read"}"#])),
            ("an env value", adding("env", .object(["A": .string("one")]), to: local("node", [])),
             adding("env", .object(["A": .string("two")]), to: local("node", []))),
            ("a header value", adding("headers", .object(["Authorization": .string("Bearer one")]), to: .object(["url": .string("https://h.example/mcp")])),
             adding("headers", .object(["Authorization": .string("Bearer two")]), to: .object(["url": .string("https://h.example/mcp")]))),
            ("a bridged env value", remote([], env: ["DEBUG": .string("1")]), remote([], env: ["DEBUG": .string("2")])),
            ("a bearer token", splunk(url: "https://h.example/mcp", token: "one"), splunk(url: "https://h.example/mcp", token: "two")),
            ("arguments split differently", local("node", ["--name", "x y"]), local("node", ["--name", "x", "y"])),
            ("-y dropped", remote([]), remote([], bridge: ["mcp-remote"])),
            ("a header beside an OAuth client", remote(["--static-oauth-client-info", info, "--header", "X:${AUTH_HEADER}"], env: ["AUTH_HEADER": .string("v1")]),
             remote(["--static-oauth-client-info", info, "--header", "X:${AUTH_HEADER}"], env: ["AUTH_HEADER": .string("v2")])),
            ("an empty env", local("node", []), adding("env", .object([:]), to: local("node", []))),
            ("a non-string env value", remote([], env: ["RETRIES": .int(3)]), remote([], env: ["RETRIES": .int(4)])),
            ("a url key beside the bridge", remote([]), adding("url", .string("https://h.example/mcp"), to: remote([]))),
        ]
        for (name, before, after) in edits {
            for exposure in [ControlMasking.Exposure.masked, .dialog, .revealed] {
                XCTAssertFalse(ControlMasking.changeLines(from: before, to: after, exposure: exposure).isEmpty, "\(name), \(exposure)")
                XCTAssertEqual(ControlMasking.changeLines(from: before, to: before, exposure: exposure), [], "\(name), \(exposure)")
            }
        }
        XCTAssertEqual(ControlMasking.changeLines(from: edits[0].1, to: edits[0].2), ["cwd", "  − /a", "  + /b"])
        XCTAssertEqual(ControlMasking.changeLines(from: edits[1].1, to: edits[1].2), ["type", "  − sse", "  + http"])
        XCTAssertEqual(ControlMasking.changeLines(from: edits[2].1, to: edits[2].2),
                       ["args", "  − --static-oauth-client-info •••", "  + --static-oauth-client-info •••"])
        XCTAssertEqual(ControlMasking.changeLines(from: edits[3].1, to: edits[3].2),
                       ["args", "  − --static-oauth-client-metadata •••", "  + --static-oauth-client-metadata •••"])
        XCTAssertEqual(ControlMasking.changeLines(from: edits[3].1, to: edits[3].2, exposure: .dialog), [
            "args",
            #"  − --static-oauth-client-metadata {"redirect_uri":"https://a.example/cb","scope":"read"}"#,
            #"  + --static-oauth-client-metadata {"redirect_uri":"https://evil.example/cb","scope":"read"}"#,
        ])
        for exposure in [ControlMasking.Exposure.masked, .dialog] {
            XCTAssertEqual(ControlMasking.changeLines(from: edits[4].1, to: edits[4].2, exposure: exposure),
                           ["env", "  − A = ••• (set)", "  + A = ••• (set)"], "a secret that changed shows as changed, still masked")
            XCTAssertEqual(ControlMasking.changeLines(from: adding("apiKey", .string("one"), to: local("node", [])),
                                                      to: adding("apiKey", .string("two"), to: local("node", [])), exposure: exposure),
                           ["apiKey", "  − ••• (set)", "  + ••• (set)"])
        }
        XCTAssertEqual(ControlMasking.changeLines(from: edits[4].1, to: edits[4].2, exposure: .revealed), ["env", "  − A = one", "  + A = two"])
    }

    /// A key no field models shows its value as one line of JSON, written the same on both platforms.
    func testAnUnmodelledKeyShowsItsValueAsJSON() {
        let metadata: JSONValue = .object(["n": .int(1), "name": .string("café"), "redirect": .string("https://a.example/cb")])
        for exposure in [ControlMasking.Exposure.masked, .dialog, .revealed] {
            XCTAssertEqual(ControlMasking.changeLines(from: notion, to: adding("metadata", metadata, to: notion), exposure: exposure),
                           ["metadata", #"  + {"n":1,"name":"café","redirect":"https://a.example/cb"}"#], "\(exposure)")
        }
        let nested: JSONValue = .object(["a": .array([.double(1.5), .bool(true), .null]), "q": .string(#"say "hi" \ now"#)])
        XCTAssertEqual(ControlMasking.changeLines(from: notion, to: adding("nested", nested, to: notion)),
                       ["nested", #"  + {"a":[1.5,true,null],"q":"say \"hi\" \\ now"}"#])
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
        try assertHidden(["987654321"], in: .object(["command": .string("node"), "args": .array([.string("--token"), .int(987654321)])]))
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

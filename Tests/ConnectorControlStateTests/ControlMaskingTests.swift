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

    /// A config with a command and nothing else: an empty `args` array is a key of its own to the lines.
    private func bare(_ command: String) -> JSONValue {
        .object(["command": .string(command)])
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
    /// only an environment value under a secret's name, header values, the OAuth client secret and a
    /// key named for a secret stay masked.
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
            #"args      run -e API_KEY=hunter2 --token abc https://u:pw@h.example/x?k=v --header 'X-Key: •••' --static-oauth-client-info {"client_id":"a","client_secret":"set"} img"#,
            "env       GITHUB_TOKEN = ••• (set)",
            "apiKey    ••• (set)",
        ])
        let oauth = RemotePattern.encode(RemoteConfig(url: "https://h.example/mcp?tenant=acme",
                                                      auth: .oauthClient(clientID: "cid", clientSecret: "csec", scopes: "read")))
        XCTAssertEqual(ControlMasking.codeLines(oauth, exposure: .dialog), [
            "type     remote, through mcp-remote",
            "url      https://h.example/mcp?tenant=acme",
            "oauth    client cid, secret ••• (set)",
            "scopes   read",
        ])
        XCTAssertEqual(ControlMasking.codeLines(local("node", [#"--static-oauth-client-info={"client_id":"a","client_secret":"csec"}"#]),
                                                exposure: .dialog), ["command   node", #"args      --static-oauth-client-info={"client_id":"a","client_secret":"set"}"#])
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

    /// What would run includes every key, not only those the lines model: a key no field shows
    /// gets a line of its own, and so does an argument mcp-remote's decoded form does not carry.
    func testCodeLinesShowEveryKeyAndArgumentTheFieldsDoNot() {
        let cwd = adding("cwd", .string("/srv/app"), to: local("node", ["s.js"]))
        XCTAssertEqual(ControlMasking.codeLines(cwd, exposure: .dialog), ["command   node", "args      s.js", "cwd       /srv/app"])
        let sse = adding("type", .string("sse"), to: notion)
        XCTAssertEqual(ControlMasking.codeLines(sse, exposure: .dialog), [
            "type      remote",
            "          sse",
            "url       https://mcp.notion.com/mcp?x=1",
            "headers   Authorization = ••• (set)",
            "          X-Empty = needs your value",
        ])
        let bridged = adding("cwd", .string("/tmp"),
                             to: remote(["--static-oauth-client-metadata", #"{"redirect_uri":"https://evil.example/cb"}"#]))
        XCTAssertEqual(ControlMasking.codeLines(bridged, exposure: .dialog), [
            "type   remote, through mcp-remote",
            "url    https://h.example/mcp",
            #"args   --static-oauth-client-metadata {"redirect_uri":"https://evil.example/cb"}"#,
            "cwd    /tmp",
        ])
        let secret: JSONValue = .object(["command": .string("node"), "apiKey": .string("topsecret"),
                                         "auth": .object(["user": .string("bob"), "pass": .string("hunter2")])])
        XCTAssertEqual(ControlMasking.codeLines(secret, exposure: .dialog),
                       ["command   node", #"apiKey    ••• (set)"#, #"auth      {"pass":"set","user":"set"}"#])
        for exposure in [ControlMasking.Exposure.masked, .dialog, .revealed] {
            let lines = [cwd, sse, bridged, secret].map { ControlMasking.codeLines($0, exposure: exposure).joined(separator: "\n") }
            XCTAssertTrue(lines[0].contains("cwd       /srv/app"), "\(exposure)")
            XCTAssertTrue(lines[1].contains("          sse"), "\(exposure)")
            XCTAssertTrue(lines[2].contains("args   --static-oauth-client-metadata ") && lines[2].contains("cwd    /tmp"), "\(exposure)")
            XCTAssertTrue(lines[3].contains("apiKey    "), "\(exposure)")
            XCTAssertEqual(lines[3].contains("topsecret") || lines[3].contains("hunter2"), exposure == .revealed, "\(exposure)")
        }
    }

    /// In the dialog a single value reads one way only, as an argument does: the command, a URL, a
    /// key's text value and a variable's name are quoted, with \ and ' escaped, when they hold a
    /// space, a quote or anything shown as an escape — so a newline and the two characters \n read
    /// apart, and a run of spaces cannot pass for the start of another row. What Claude and the
    /// command line read is unchanged.
    func testTheDialogShowsASingleValueOneWayOnly() {
        let literal = bare(#"run\n"#)
        let newline = bare("run\n")
        XCTAssertEqual(ControlMasking.codeLines(literal, exposure: .dialog), [#"command   run\n"#])
        XCTAssertEqual(ControlMasking.codeLines(newline, exposure: .dialog), [#"command   'run\n'"#])
        let spaced = adding("cwd", .string("/srv        env   X = y"), to: bare("node"))
        XCTAssertEqual(ControlMasking.codeLines(spaced, exposure: .dialog), ["command   node", "cwd       '/srv        env   X = y'"])
        let named = adding("env", .object(["A B = c": .string("v")]), to: bare("node"))
        XCTAssertEqual(ControlMasking.codeLines(named, exposure: .dialog), ["command   node", "env       'A B = c' = v"])
        let address = JSONValue.object(["url": .string("https://h.example/a b")])
        XCTAssertEqual(ControlMasking.codeLines(address, exposure: .dialog), ["type   remote", "url    'https://h.example/a b'"])
        let keyed = adding("a b", .string("v"), to: bare("node"))
        XCTAssertEqual(ControlMasking.codeLines(keyed, exposure: .dialog), ["command   node", "'a b'     v"],
                       "a key that labels a row of its own is quoted too")
        XCTAssertEqual(ControlMasking.codeLines(newline), [#"command   run\n"#], "the masked lines are unchanged")
        XCTAssertEqual(ControlMasking.codeLines(literal), [#"command   run\n"#])
    }

    /// The dialog masks the OAuth client secret, not the client: the client info shows with only
    /// its secret, and any value under a key named for a secret, masked. Text that is not a JSON
    /// object stays masked whole, and tool output masks it whole either way.
    func testTheDialogShowsTheOAuthClientInfoButItsSecret() {
        let info = #"{"client_id":"cid9","client_secret":"csec9","redirect_uris":["https://a.example/cb"]}"#
        XCTAssertEqual(ControlMasking.codeLines(local("node", ["--static-oauth-client-info", info]), exposure: .dialog), [
            "command   node",
            #"args      --static-oauth-client-info {"client_id":"cid9","client_secret":"set","redirect_uris":["https://a.example/cb"]}"#,
        ])
        XCTAssertEqual(ControlMasking.codeLines(local("node", ["--static-oauth-client-info", info]), exposure: .masked).last,
                       "args      --static-oauth-client-info •••")
        for text in ["not-json-csec8", #""csec7""#, #"["csec6"]"#] {
            XCTAssertEqual(ControlMasking.codeLines(local("node", ["--static-oauth-client-info", text]), exposure: .dialog).last,
                           "args      --static-oauth-client-info •••", text)
            XCTAssertEqual(ControlMasking.codeLines(local("node", ["--static-oauth-client-info=" + text]), exposure: .dialog).last,
                           "args      --static-oauth-client-info=•••", text)
        }
        let bridged = remote(["--static-oauth-client-info", #"{"client_id":"a","client_secret":"csec5","extra":1}"#])
        XCTAssertEqual(ControlMasking.codeLines(bridged, exposure: .dialog), [
            "type    remote, through mcp-remote",
            "url     https://h.example/mcp",
            "oauth   client a, secret ••• (set)",
            #"args    --static-oauth-client-info {"client_id":"a","client_secret":"set","extra":1}"#,
        ])
    }

    /// What the lines show reads back as what would run: two argument lists read differently, an
    /// empty argument shows, and a newline or a bidi control in a value cannot forge or hide a row.
    func testTheLinesCannotBeMisreadOrForged() {
        let spaced = local("node", ["--name", "x y"])
        let split = local("node", ["--name", "x", "y"])
        let forged = local("node", ["s.js", "x\nenv       SAFE = ••• (set)"])
        let keyed: JSONValue = .object(["command": .string("node"), "x\nenv": .string("v\nSAFE = ••• (set)"),
                                        "url": .string("https://h.example/\u{202E}gpj.exe")])
        for exposure in [ControlMasking.Exposure.masked, .dialog, .revealed] {
            XCTAssertEqual(ControlMasking.codeLines(spaced, exposure: exposure).last, "args      --name 'x y'", "\(exposure)")
            XCTAssertEqual(ControlMasking.codeLines(split, exposure: exposure).last, "args      --name x y", "\(exposure)")
            XCTAssertEqual(ControlMasking.changeLines(from: spaced, to: split, exposure: exposure),
                           ["args", "  − --name 'x y'", "  + --name x y"], "\(exposure)")
            XCTAssertEqual(ControlMasking.codeLines(local("node", ["a", "", "b"]), exposure: exposure).last, "args      a '' b", "\(exposure)")
            XCTAssertEqual(ControlMasking.codeLines(forged, exposure: exposure),
                           ["command   node", #"args      s.js 'x\nenv       SAFE = ••• (set)'"#], "\(exposure)")
            XCTAssertEqual(ControlMasking.changeLines(from: .object(["command": .string("node")]), to: forged, exposure: exposure),
                           ["args", #"  + s.js 'x\nenv       SAFE = ••• (set)'"#], "\(exposure)")
            for line in ControlMasking.codeLines(keyed, exposure: exposure) + ControlMasking.changeLines(from: .object([:]), to: keyed, exposure: exposure) {
                XCTAssertFalse(line.unicodeScalars.contains { $0 == "\n" || $0 == "\u{202E}" }, "\(exposure): \(line)")
            }
        }
        XCTAssertEqual(ControlMasking.codeLines(keyed, exposure: .dialog),
                       ["command    node", #"url        'https://h.example/\u{202E}gpj.exe'"#, #"'x\nenv'   'v\nSAFE = ••• (set)'"#])
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

    /// The dialog shows an environment value as written, quoted as an argument is, since a variable
    /// can change what runs (`NODE_OPTIONS`, `PATH`, `LD_PRELOAD`): every one but a variable whose
    /// name reads as a secret's. A header's value stays masked whatever its name, and tool output and
    /// `show --reveal` are as they were.
    func testTheDialogShowsAnEnvironmentValueUnlessItsNameIsASecrets() {
        let node: JSONValue = .object(["command": .string("node"), "env": .object([
            "NODE_OPTIONS": .string("--require ./hook.js"), "PATH": .string("/opt/x/bin"), "PORT": .int(8080),
            "EMPTY": .string(""), "API_KEY": .string("hunter2"), "DB_PASSWORD": .string(""),
        ])])
        XCTAssertEqual(ControlMasking.codeLines(node, exposure: .dialog), [
            "command   node",
            "env       API_KEY = ••• (set)",
            "          DB_PASSWORD = needs your value",
            "          EMPTY = ''",
            "          NODE_OPTIONS = '--require ./hook.js'",
            "          PATH = /opt/x/bin",
            "          PORT = 8080",
        ])
        XCTAssertEqual(ControlMasking.codeLines(node), [
            "command   node",
            "env       API_KEY = ••• (set)",
            "          DB_PASSWORD = needs your value",
            "          EMPTY = needs your value",
            "          NODE_OPTIONS = ••• (set)",
            "          PATH = ••• (set)",
            "          PORT = ••• (set)",
        ], "tool output masks every one")
        XCTAssertEqual(ControlMasking.codeLines(node, exposure: .revealed)[4], "          NODE_OPTIONS = --require ./hook.js")
        let bridged = remote(["--header", "Authorization:${AUTH_HEADER}"],
                             env: ["AUTH_HEADER": .string("Bearer s3cr3t"), "NODE_OPTIONS": .string("--require ./hook.js")])
        XCTAssertEqual(ControlMasking.codeLines(bridged, exposure: .dialog), [
            "type      remote, through mcp-remote",
            "url       https://h.example/mcp",
            "headers   Authorization = ••• (set)",
            "env       NODE_OPTIONS = '--require ./hook.js'",
        ])
        let native: JSONValue = .object(["url": .string("https://h.example/mcp"), "headers": .object(["X-Region": .string("eu")])])
        XCTAssertEqual(ControlMasking.codeLines(native, exposure: .dialog).last, "headers   X-Region = ••• (set)", "a header stays masked")
        let before = adding("env", .object(["NODE_OPTIONS": .string("--require ./a.js"), "API_KEY": .string("secret-one")]), to: local("node", []))
        let after = adding("env", .object(["NODE_OPTIONS": .string("--require ./b.js"), "API_KEY": .string("secret-two")]), to: local("node", []))
        XCTAssertEqual(ControlMasking.changeLines(from: before, to: after, exposure: .dialog), [
            "env",
            "  − API_KEY = ••• (set)",
            "  − NODE_OPTIONS = '--require ./a.js'",
            "  + API_KEY = ••• (set)",
            "  + NODE_OPTIONS = '--require ./b.js'",
        ])
        XCTAssertEqual(ControlMasking.changeLines(from: local("node", []), to: before, exposure: .dialog),
                       ["env", "  + API_KEY = ••• (set)", "  + NODE_OPTIONS = '--require ./a.js'"])
        let dialog = (ControlMasking.codeLines(node, exposure: .dialog) + ControlMasking.changeLines(from: before, to: after, exposure: .dialog))
            .joined(separator: "\n")
        for secret in ["hunter2", "secret-one", "secret-two"] { XCTAssertFalse(dialog.contains(secret), secret) }
        let masked = (ControlMasking.codeLines(node) + ControlMasking.changeLines(from: before, to: after)).joined(separator: "\n")
        for value in ["hook.js", "/opt/x/bin", "8080", "a.js", "b.js", "secret-one", "secret-two"] { XCTAssertFalse(masked.contains(value), value) }
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
            ("a secret's env value", adding("env", .object(["API_KEY": .string("one")]), to: local("node", [])),
             adding("env", .object(["API_KEY": .string("two")]), to: local("node", []))),
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
                           ["env", "  − API_KEY = ••• (set)", "  + API_KEY = ••• (set)"], "a secret that changed shows as changed, still masked")
            XCTAssertEqual(ControlMasking.changeLines(from: adding("apiKey", .string("one"), to: local("node", [])),
                                                      to: adding("apiKey", .string("two"), to: local("node", [])), exposure: exposure),
                           ["apiKey", "  − ••• (set)", "  + ••• (set)"])
        }
        XCTAssertEqual(ControlMasking.changeLines(from: edits[4].1, to: edits[4].2, exposure: .revealed), ["env", "  − API_KEY = one", "  + API_KEY = two"])
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

    /// `env` and `headers` hold secrets wherever they sit, not only at the top of a config.
    func testEnvironmentAndHeadersUnderAnotherKeyAreMasked() throws {
        let nested: JSONValue = .object([
            "command": .string("node"), "args": .array([.string("s.js")]),
            "options": .object(["env": .object(["DB": .string("hunter2a")]), "headers": .object(["X-Custom": .string("hunter2b")])]),
        ])
        let transport: JSONValue = .object([
            "url": .string("https://h.example/mcp"),
            "transport": .object(["headers": .object(["X-Custom": .string("hunter2c")])]),
        ])
        try assertHidden(["hunter2a", "hunter2b"], in: nested)
        try assertHidden(["hunter2c"], in: transport)
        XCTAssertEqual(ControlMasking.codeLines(nested, exposure: .dialog),
                       ["command   node", "args      s.js", #"options   {"env":{"DB":"set"},"headers":{"X-Custom":"set"}}"#])
        XCTAssertEqual(ControlMasking.codeLines(transport, exposure: .dialog),
                       ["type        remote", "url         https://h.example/mcp", #"transport   {"headers":{"X-Custom":"set"}}"#])
        let dialog = (ControlMasking.changeLines(from: .object([:]), to: nested, exposure: .dialog)
            + ControlMasking.changeLines(from: .object([:]), to: transport, exposure: .dialog)).joined(separator: "\n")
        for secret in ["hunter2a", "hunter2b", "hunter2c"] { XCTAssertFalse(dialog.contains(secret), secret) }
        XCTAssertTrue(dialog.contains(#""DB":"set""#) && dialog.contains(#""X-Custom":"set""#))
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

    /// The heuristics read a name unit by unit on both platforms, so a combining mark after it
    /// does not unmask its value on the Mac only.
    func testACombiningMarkAfterASecretNameKeepsItsValueMasked() throws {
        XCTAssertEqual(ControlMasking.argument("--api-key\u{301}=hunter2y"), "--api-key\u{301}=•••")
        XCTAssertEqual(ControlMasking.arguments(["--api-key\u{301}", "hunter2x"]), ["--api-key\u{301}", "•••"])
        XCTAssertEqual(ControlMasking.argument("API_TOKEN\u{301}=hunter2z"), "API_TOKEN\u{301}=•••")
        try assertHidden(["hunter2x", "hunter2y", "hunter2z"],
                         in: local("node", ["--api-key\u{301}=hunter2y", "--api-key\u{301}", "hunter2x", "API_TOKEN\u{301}=hunter2z"]))
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

    /// A shell line packed into one argument (`bash -c "…"`, `cmd /c …`), or a whole command line
    /// written as the command, is masked word by word as a shell passes its words, so a flag named
    /// for a secret among them still hides the word after it and a URL among them is not the whole.
    func testAShellLineInOneArgumentIsMaskedWordByWord() throws {
        XCTAssertEqual(ControlMasking.arguments(["-c", "npx -y s --token hunter2abc"]), ["-c", "npx -y s --token •••"])
        let bash = "npx -y mcp-remote https://h/sse --header 'Authorization: Bearer eyJhbGc.eyJzdWI.sig-x_y'"
        XCTAssertEqual(ControlMasking.arguments(["-c", bash]),
                       ["-c", "npx -y mcp-remote https://h/sse --header 'Authorization: •••'"])
        XCTAssertEqual(ControlMasking.argument("API_KEY=sk_live_x npx server https://h.example/mcp"),
                       "API_KEY=••• npx server https://h.example/mcp")
        XCTAssertEqual(ControlMasking.codeLines(bare("npx -y s --token hunter2abc")), ["command   npx -y s --token •••"])
        try assertHidden(["hunter2abc"], in: local("bash", ["-c", "npx -y s --token hunter2abc"]))
        try assertHidden(["eyJhbGc", "sig-x_y"], in: local("bash", ["-c", bash]))
        try assertHidden(["sk_live_x"], in: local("sh", ["-c", "API_KEY=sk_live_x npx server https://h.example/mcp"]))
        try assertHidden(["hunter2abc"], in: local("cmd", ["/c", "npx -y s --api-key hunter2abc"]))
        try assertHidden(["hunter2abc"], in: bare("npx -y s --token hunter2abc"))
    }

    /// A value with spaces and no secret reads as written, quotes and all.
    func testAShellLineWithNoSecretReadsAsWritten() {
        for text in ["hello world", "echo \"a b\" 'c d'", "C:\\Program Files\\nodejs\\node.exe", "npx -y server --port 8080"] {
            XCTAssertEqual(ControlMasking.argument(text), text)
        }
    }

    /// JSON passed as an argument — Smithery's `run <server> --config '{"apiKey":"…"}'` — is masked
    /// leaf by leaf, by key, as a config is; JSON with nothing to mask reads as written.
    func testAJSONArgumentIsMaskedByKey() throws {
        let smithery = ["-y", "@smithery/cli", "run", "@x/server", "--config", #"{"apiKey":"sk_abc123","region":"us"}"#]
        XCTAssertEqual(ControlMasking.arguments(smithery),
                       ["-y", "@smithery/cli", "run", "@x/server", "--config", #"{"apiKey":"set","region":"us"}"#])
        XCTAssertEqual(ControlMasking.argument(#"[{"token": "hunter2x"}, "plain"]"#), #"[{"token":"set"},"plain"]"#)
        XCTAssertEqual(ControlMasking.argument(#"{"region": "us", "port": 8080}"#), #"{"region": "us", "port": 8080}"#)
        try assertHidden(["sk_abc123"], in: local("npx", smithery))
        try assertHidden(["sk_abc123"], in: bare(#"npx -y @smithery/cli run @x/server --config '{"apiKey": "sk_abc123"}'"#))
    }

    /// JSON nested deeper than either platform reads is masked whole, alike on both: what it holds
    /// cannot be read leaf by leaf.
    func testJSONNestedDeeperThan64IsMaskedWholeAlike() throws {
        let deep = String(repeating: #"{"a":"#, count: 69) + #"{"apiKey":"sk_abc123"}"# + String(repeating: "}", count: 69)
        XCTAssertEqual(ControlMasking.argument(deep), ControlMasking.mask)
        XCTAssertEqual(ControlMasking.arguments(["--static-oauth-client-info", deep], exposure: .dialog),
                       ["--static-oauth-client-info", ControlMasking.mask])
        try assertHidden(["sk_abc123"], in: local("npx", ["--config", deep]))
    }

    /// A user name or password holding an unencoded `#`, `?` or `/` hides everything from the
    /// scheme to the last `@`, since where it ends cannot be told from where the host's path
    /// starts; an `@` in a path is hidden with it, which shows less and never more.
    func testUserInfoHoldingAURLDelimiterIsMaskedToTheLastAt() throws {
        XCTAssertEqual(ControlMasking.url("postgres://admin:P#ssw0rd@db/x"), "postgres://•••@db/x")
        XCTAssertEqual(ControlMasking.url("https://user:pa/ss@host/x"), "https://•••@host/x")
        XCTAssertEqual(ControlMasking.url("https://user:pa?ss@host/x?token=abc#f"), "https://•••@host/x?token=•••#f")
        XCTAssertEqual(ControlMasking.url("https://unpkg.com/@scope/pkg"), "https://•••@scope/pkg")
        XCTAssertEqual(ControlMasking.url("https://user:hunter2@host/x"), "https://user:•••@host/x")
        try assertHidden(["P#ssw0rd", "ssw0rd"], in: local("psql", ["postgres://admin:P#ssw0rd@db/x"]))
        try assertHidden(["pa/ss"], in: .object(["url": .string("https://user:pa/ss@host/x")]))
    }
}

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
    /// space, a quote or anything shown as an escape - so a newline and the two characters \n read
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

    /// Every masked view of `config` - its structured form, its code lines and its change lines
    /// from nothing - without any of `secrets`.
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
        XCTAssertEqual(ControlMasking.argument("API_KEY=sk_live_x npx server https://h.example/mcp"), "API_KEY=•••")
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

    /// JSON passed as an argument - Smithery's `run <server> --config '{"apiKey":"…"}'` - is masked
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

    /// A variable whose value names code to run shows in full in the confirmation dialog even when
    /// its name reads as a secret's (`GIT_ASKPASS` holds "pass"), so the person allowing sees what
    /// would run; what Claude reads still masks it, as every environment value.
    func testAVariableThatRunsAProgramShowsInTheDialogAndStaysMaskedForClaude() {
        let env: [String: JSONValue] = [
            "GIT_ASKPASS": .string("/tmp/evil.sh"), "ssh_askpass": .string("/tmp/a.sh"), "SSH_ASKPASS_REQUIRE": .string("force"),
            "GIT_CONFIG_KEY_0": .string("core.sshCommand"), "API_TOKEN": .string("tok123"),
        ]
        let config = JSONValue.object(["command": .string("git-mcp"), "env": .object(env)])
        let dialog = ControlMasking.codeLines(config, exposure: .dialog)
        for line in ["GIT_ASKPASS = /tmp/evil.sh", "GIT_CONFIG_KEY_0 = core.sshCommand", "SSH_ASKPASS_REQUIRE = force",
                     "ssh_askpass = /tmp/a.sh", "API_TOKEN = ••• (set)"] {
            XCTAssertTrue(dialog.contains { $0.hasSuffix(line) }, line)
        }
        XCTAssertTrue(ControlMasking.changeLines(from: .object(["command": .string("git-mcp")]), to: config, exposure: .dialog)
            .contains { $0.hasSuffix("GIT_ASKPASS = /tmp/evil.sh") })
        let remote = remote([], env: ["GIT_ASKPASS": .string("/tmp/evil.sh")])
        XCTAssertTrue(ControlMasking.codeLines(remote, exposure: .dialog).contains { $0.hasSuffix("GIT_ASKPASS = /tmp/evil.sh") })
        let masked = (ControlMasking.codeLines(config) + ControlMasking.codeLines(remote)).joined(separator: "\n")
        for value in ["/tmp/evil.sh", "/tmp/a.sh", "force", "core.sshCommand", "tok123"] {
            XCTAssertFalse(masked.contains(value), value)
        }
        XCTAssertEqual(ControlMasking.maskedConfig(config).value(at: JSONPointer(["env", "GIT_ASKPASS"])), .string("set"))
    }

    /// An argument that reads as a secret whole (`Bearer x`, a token with spaces) stays masked
    /// whole: splitting it would show the token beside the scheme word.
    func testAnArgumentThatReadsAsASecretWholeStaysMaskedWhole() throws {
        XCTAssertEqual(ControlMasking.argument("Bearer abc123"), ControlMasking.mask)
        XCTAssertEqual(ControlMasking.arguments(["--headers", "X-Team", "Bearer abc123"]), ["--headers", "X-Team", ControlMasking.mask])
        XCTAssertEqual(ControlMasking.argument("Bearer ${TOKEN}"), "Bearer ${TOKEN}")
        try assertHidden(["abc123"], in: local("node", ["s.js", "Bearer abc123"]))
    }

    /// The header forms other bridges take: a name and its value as two words (mcp-proxy's
    /// `--headers K V`, `-H K V`, or `Authorization:` then `Bearer x` as a shell splits them), a
    /// scheme word before the value, and `--header=…`. A header not named for a secret, and a
    /// value that is a reference, read as written.
    func testOtherHeaderFormsAreMasked() throws {
        XCTAssertEqual(ControlMasking.arguments(["--headers", "Authorization", "Bearer abc123"]), ["--headers", "Authorization", "•••"])
        XCTAssertEqual(ControlMasking.arguments(["-H", "X-Api-Key", "abc123"]), ["-H", "X-Api-Key", "•••"])
        XCTAssertEqual(ControlMasking.arguments(["-H", "cookie", "sid=abc123"]), ["-H", "cookie", "•••"])
        XCTAssertEqual(ControlMasking.arguments(["--header", "Authorization", "Bearer", "abc123"]),
                       ["--header", "Authorization", "Bearer", "•••"])
        XCTAssertEqual(ControlMasking.arguments(["--header", "Authorization:", "basic", "abc123"]),
                       ["--header", "Authorization:", "basic", "•••"])
        XCTAssertEqual(ControlMasking.arguments(["--header=Authorization: Bearer abc123"]), ["--header=Authorization: •••"])
        XCTAssertEqual(ControlMasking.arguments(["-H=X-Api-Key: abc123", "--headers=Cookie: a=b"]), ["-H=X-Api-Key: •••", "--headers=Cookie: •••"])
        let plain = ["-H", "X-Team", "blue", "-H", "Authorization", "${TOKEN}", "-H", "0.0.0.0", "-p", "80", "--header", "Authorization", "Bearer", "$TOKEN"]
        XCTAssertEqual(ControlMasking.arguments(plain), plain)
        XCTAssertEqual(ControlMasking.arguments(["--headers", "X-Api-Key", "abc123", "--header=Authorization: Bearer abc123"], exposure: .dialog),
                       ["--headers", "X-Api-Key", "•••", "--header=Authorization: •••"])
        try assertHidden(["abc123"], in: local("npx", ["mcp-proxy", "--headers", "Authorization", "Bearer abc123", "-H", "Cookie", "sid=abc123"]))
        try assertHidden(["abc123"], in: local("npx", ["supergateway", "--header=Authorization: Bearer abc123"]))
        try assertHidden(["abc123"], in: local("sh", ["-c", "npx supergateway --header Authorization: Bearer abc123"]))
    }

    /// A word written against a placeholder is not part of the reference: it can be the secret.
    func testAWordGluedToAReferenceIsMasked() throws {
        XCTAssertEqual(ControlMasking.argument("--password=${CC_NEEDS:PW}hunter"), "--password=•••")
        XCTAssertEqual(ControlMasking.arguments(["--password", "${CC_NEEDS:PW}hunter"]), ["--password", "•••"])
        XCTAssertEqual(ControlMasking.argument("--password=${CC_NEEDS:PW}"), "--password=${CC_NEEDS:PW}")
        try assertHidden(["hunter"], in: local("node", ["s.js", "--password=${CC_NEEDS:PW}hunter", "--token", "${T}hunter"]))
    }

    /// A secret value with spaces is hidden whole: the shell line's reading never shows a character
    /// the one-word reading hid, and `name=value` is read before a URL in the value.
    func testASecretValueWithSpacesStaysHiddenWhole() throws {
        XCTAssertEqual(ControlMasking.argument("--password=correct horse battery staple"), "--password=•••")
        XCTAssertEqual(ControlMasking.arguments(["-e", "API_KEY=my secret value"]), ["-e", "API_KEY=•••"])
        XCTAssertEqual(ControlMasking.argument("--client-secret=abc def ghi"), "--client-secret=•••")
        XCTAssertEqual(ControlMasking.argument(#"{"note":"--password=a b"}"#), #"{"note":"--password=•••"}"#)
        XCTAssertEqual(ControlMasking.argument("--password=a b https://x.example"), "--password=•••")
        let postgres = ControlMasking.argument("postgres://admin:pa ss@db/x?sslkey=abc")
        XCTAssertFalse(postgres.contains("pa ss") || postgres.contains("abc"), postgres)
        XCTAssertFalse(ControlMasking.argument("postgres://admin:pa ss@db/x?sslkey=abc --token t1").contains("pa"))
        try assertHidden(["horse", "battery", "staple", "secret value", "def", "ghi", "pa ss", "sslkey=abc"],
                         in: local("node", ["--password=correct horse battery staple", "-e", "API_KEY=my secret value",
                                            "--client-secret=abc def ghi", "--password=a b https://x.example",
                                            "postgres://admin:pa ss@db/x?sslkey=abc"]))
    }

    /// `-H Authorization:Bearer x`, the scheme written against the name's colon, hides the value after it.
    func testANameAndSchemeWordHidesTheValueAfterIt() {
        XCTAssertEqual(ControlMasking.arguments(["-H", "Authorization:Bearer", "abc123xyz"]), ["-H", "Authorization: •••", "•••"])
        XCTAssertEqual(ControlMasking.arguments(["-H", "X-Api-Key: basic", "abc123xyz"]), ["-H", "X-Api-Key: •••", "•••"])
        XCTAssertEqual(ControlMasking.arguments(["-H", "X-Team:blue", "abc"]), ["-H", "X-Team: •••", "abc"])
    }

    /// A scheme word after a flag named for a secret does not use up the mask: the word after it
    /// is hidden too, and so is curl's `-H'Authorization: Bearer x'`, which a shell splits into
    /// `-HAuthorization:`, `Bearer` and `x`.
    func testASchemeWordAfterASecretNamedFlagHidesTheWordAfterIt() throws {
        XCTAssertEqual(ControlMasking.arguments(["--token", "Bearer", "hunter2"]), ["--token", "•••", "•••"])
        XCTAssertEqual(ControlMasking.arguments(["--token", "Bearer", "--verbose"]), ["--token", "•••", "--verbose"])
        XCTAssertEqual(ControlMasking.arguments(["--token", "Bearer", "${TOKEN}"]), ["--token", "•••", "${TOKEN}"])
        for args in [["--token", "Bearer", "hunter2"], ["-c", "curl -H'Authorization: Bearer hunter2' https://x"]] {
            XCTAssertFalse(ControlMasking.arguments(args).joined(separator: " ").contains("hunter2"), "\(args)")
            try assertHidden(["hunter2"], in: local("sh", args))
        }
    }

    /// A flag with `=` whose glued value leaves the secret to the next word hides that word, past a
    /// scheme word, as the flag on its own does.
    func testAFlagWithItsValueGluedOnHidesTheSecretAfterIt() throws {
        let cases: [[String]] = [
            ["--header=Authorization:", "Bearer", "x9secret"],
            ["--headers=Authorization:", "Bearer", "hunter2"],
            ["--header=Authorization: Bearer", "hunter2"],
            ["--token=Bearer", "hunter2"],
            ["--token=", "hunter2"],
            ["-c", "curl --header=Authorization: Bearer hunter2 https://x"],
        ]
        for args in cases {
            let shown = ControlMasking.arguments(args).joined(separator: " ")
            XCTAssertFalse(shown.contains("hunter2") || shown.contains("x9secret"), "\(args) -> \(shown)")
            try assertHidden(["hunter2", "x9secret"], in: local("sh", args))
        }
        XCTAssertEqual(ControlMasking.arguments(["--token=Bearer", "${TOKEN}"]), ["--token=•••", "${TOKEN}"])
        XCTAssertEqual(ControlMasking.arguments(["--header=X-Team:", "blue"]), ["--header=X-Team:", "blue"])
        XCTAssertEqual(ControlMasking.arguments(["--token=abc", "server.js"]), ["--token=•••", "server.js"])
    }

    /// Text that starts with a bracket but is not JSON - an IPv6 literal, a glob, a template, an INI
    /// section, a PowerShell type literal, a test bracket - reads as written, as an argument, inside
    /// a shell line and as any other string in a config.
    func testBracketedValuesThatAreNotJSONReadAsWritten() {
        XCTAssertEqual(ControlMasking.arguments(["--host", "[::1]"]), ["--host", "[::1]"])
        XCTAssertEqual(ControlMasking.arguments(["--listen", "[::]:8080"]), ["--listen", "[::]:8080"])
        XCTAssertEqual(ControlMasking.argument("{src,lib}/**/*.ts"), "{src,lib}/**/*.ts")
        let plain = [
            ["-c", "exec server --bind [::1]:8080"], ["--include", "{src,lib}/**/*.ts"], ["--format", "{{json .}}"], ["[default]"],
            ["-Command", "[Net.ServicePointManager]::SecurityProtocol = 'Tls12'; iex x"],
            ["-c", "python -c '[print(i) for i in x]'"], ["-c", "[ -f x ] && run"], ["--config", "{region: 'us'}"],
        ]
        for args in plain { XCTAssertEqual(ControlMasking.arguments(args), args) }
        let config = JSONValue.object(["command": .string("x"), "cwd": .string("[workspace]"), "glob": .string("{a,b}")])
        XCTAssertEqual(ControlMasking.maskedConfig(config), config)
    }

    /// A `Name: value` argument whose name is a secret's or signs in is a header wherever it stands
    /// (M-1a), so its value is hidden after a flag the masking doesn't know; another reads as written.
    func testAHeaderShapedArgumentIsMaskedAfterAnyFlag() throws {
        XCTAssertEqual(ControlMasking.arguments(["--http-header", "X-Api-Key: hunter2"]), ["--http-header", "X-Api-Key: •••"])
        XCTAssertEqual(ControlMasking.argument("x-api-key: hunter2"), "x-api-key: •••")
        XCTAssertEqual(ControlMasking.argument("Cookie: sid=hunter2"), "Cookie: •••")
        XCTAssertEqual(ControlMasking.argument("--http-header=X-Api-Key: hunter2"), "--http-header=X-Api-Key: •••")
        for text in ["X-Team: blue", "localhost:8080", "C:\\Users\\x", "X-Api-Key: ${KEY}", "12:30"] {
            XCTAssertEqual(ControlMasking.argument(text), text)
        }
        // Only a whole word of the name counts, and a scope word or a path is not a header's value.
        let plain = [["-v", "secrets:/run/secrets"], ["-v", "ssh-keys:/root/.ssh:ro"], ["--opt", "keycloak:8080"], ["--opt", "keydb:6379"],
                     ["--opt", "author:x"], ["--opt", "keyword:x"], ["--scopes", "token:read"],
                     ["--scopes", "tokens:read users:write"]]
        for args in plain { XCTAssertEqual(ControlMasking.arguments(args), args) }
        // A digit ends a word of the name, so `auth0` reads as `auth` and its value is hidden.
        for (text, shown) in [("password:admin", "password: •••"), ("passphrase:admin", "passphrase: •••"), ("pwd:all", "pwd: •••"), ("password:/hunter2", "password: •••"), ("X-Api-Key:/Abc9xYz1QwErTy7UiOp3AsDf", "X-Api-Key: •••"), ("token1:hunter2", "token1: •••"), ("apiKey2:hunter2", "apiKey2: •••"), ("auth0:domain", "auth0: •••"), ("apiKey:hunter2", "apiKey: •••"), ("Authorization: hunter2", "Authorization: •••"),
                              ("password:hunter2", "password: •••"), ("X-Auth-Token: hunter2", "X-Auth-Token: •••")] {
            XCTAssertEqual(ControlMasking.arguments(["--opt", text]), ["--opt", shown])
        }
        try assertHidden(["hunter2"], in: local("npx", ["s", "--http-header", "X-Api-Key: hunter2", "x-api-key: hunter2"]))
        try assertHidden(["hunter2"], in: local("sh", ["-c", "npx s --http-header 'Cookie: sid=hunter2'"]))
    }

    /// A user and password with no scheme (M-1b) - after curl's `-u` or `--user`, or before an `@`
    /// as a URL's user info - hides the password; a uid:gid pair and an address read as written.
    func testACredentialPairWithNoSchemeIsMasked() throws {
        XCTAssertEqual(ControlMasking.arguments(["--user", "admin:hunter2"]), ["--user", "admin:•••"])
        XCTAssertEqual(ControlMasking.arguments(["-u", "admin:hunter2"]), ["-u", "admin:•••"])
        XCTAssertEqual(ControlMasking.argument("--user=admin:hunter2"), "--user=admin:•••")
        XCTAssertEqual(ControlMasking.argument("curl -u admin:hunter2 https://x"), "curl -u admin:••• https://x")
        XCTAssertEqual(ControlMasking.arguments(["--dsn", "admin:hunter2@db/x"]), ["--dsn", "admin:•••@db/x"])
        for args in [["-u", "1000:1000"], ["-u", "admin"], ["--user", "admin:${PW}"], ["-u", "${CC_NEEDS:creds}"], ["--user=${CC_NEEDS:creds}"], ["git@github.com:org/repo.git"], ["a@b.example"],
                     ["--user", "node:node"], ["--user", "1000:node"], ["--user", "1000:www-data"], ["--user", "www-data:www-data"], ["-update:true"], ["-useLegacy:yes"],
                     ["-url=http://localhost:8080"], ["-upstream=localhost:8080"], ["-user=node:node"]] {
            XCTAssertEqual(ControlMasking.arguments(args), args)
        }
        XCTAssertEqual(ControlMasking.arguments(["-u", "admin:123456"]), ["-u", "admin:•••"])
        XCTAssertEqual(ControlMasking.arguments(["-u", "123456:hunter2"]), ["-u", "123456:•••"])
        XCTAssertEqual(ControlMasking.argument("--user=123:hunter2"), "--user=123:•••")
        XCTAssertEqual(ControlMasking.argument("-uadmin:pa=ss"), "-uadmin:•••")
        try assertHidden(["hunter2"], in: local("sh", ["-c", "curl -u 123456:hunter2 https://x"]))
        XCTAssertEqual(ControlMasking.arguments(["-u", "https://u:hunter2@h"]), ["-u", "https://u:•••@h"])
        XCTAssertEqual(ControlMasking.arguments(["-uadmin:hunter2", "--proxy-user", "admin:hunter2"]), ["-uadmin:•••", "--proxy-user", "admin:•••"])
        XCTAssertEqual(ControlMasking.arguments(["-c", "admin:hunter2@db --token mysecretvalue"]), ["-c", "admin:•••@db --token •••"])
        XCTAssertEqual(ControlMasking.argument("--db=admin:hunter2@db --token mysecretvalue"), "--db=admin:•••@db --token •••")
        try assertHidden(["hunter2", "mysecretvalue"], in: local("sh", ["-c", "curl -uadmin:hunter2 https://x; admin:hunter2@db --token mysecretvalue"]))
        try assertHidden(["hunter2"], in: local("curl", ["-u", "admin:hunter2", "--user", "root:hunter2", "--dsn", "admin:hunter2@db/x"]))
        try assertHidden(["hunter2"], in: local("sh", ["-c", "curl -u admin:hunter2 https://x"]))
    }

    /// Text that starts with `{` or `[` and is not JSON - JSON5, unquoted keys, broken JSON - is
    /// hidden whole when a key in it is named for a secret (M-1c), and reads as written otherwise.
    func testJSONLikeTextWithASecretNamedKeyIsHidden() throws {
        XCTAssertEqual(ControlMasking.arguments(["--config", "{apiKey: 'hunter2'}"]), ["--config", "•••"])
        XCTAssertEqual(ControlMasking.argument(#"{"apiKey": "hunter2""#), "•••")
        XCTAssertEqual(ControlMasking.argument("{password=hunter2}"), "•••")
        XCTAssertEqual(ControlMasking.argument("[{'token': 'hunter2'}]"), "•••")
        XCTAssertEqual(ControlMasking.argument("{env: {DB_URL: 'hunter2'}}"), "•••")
        XCTAssertEqual(ControlMasking.argument("{Headers: {X: 'hunter2'}}"), "•••")
        XCTAssertEqual(ControlMasking.argument("{region: 'us', port: 8080}"), "{region: 'us', port: 8080}")
        try assertHidden(["hunter2"], in: local("npx", ["-y", "@smithery/cli", "run", "x", "--config", "{apiKey: 'hunter2'}"]))
        try assertHidden(["hunter2"], in: bare("npx -y @smithery/cli run x --config \"{apiKey: 'hunter2'}\""))
        try assertHidden(["hunter2"], in: .object(["command": .string("x"), "opts": .string("{apiKey: 'hunter2'}")]))
        // Known gap: unquoted in a shell line, the shell splits it at the space, so the value is a
        // word of its own after a word that is only a key.
        XCTAssertEqual(ControlMasking.argument("npx s --config {apiKey: hunter2}"), "npx s --config ••• hunter2}")
    }

    /// A shell default for a variable named for a secret (M-1d), or one that reads as a secret, is
    /// hidden; the reference itself, and any other default, read as written.
    func testAShellDefaultForASecretIsMasked() throws {
        XCTAssertEqual(ControlMasking.argument("${DB_PASSWORD:-hunter2}"), "${DB_PASSWORD:-•••}")
        XCTAssertEqual(ControlMasking.argument("${API_TOKEN-hunter2}"), "${API_TOKEN-•••}")
        XCTAssertEqual(ControlMasking.argument("--db=${DB_PASSWORD:-hunter2}"), "--db=${DB_PASSWORD:-•••}")
        XCTAssertEqual(ControlMasking.argument("${PATH_X:-sk-live-abc}"), "${PATH_X:-•••}")
        for text in ["${PORT:-8080}", "${DB_PASSWORD:-${OTHER}}", "${DB_PASSWORD}", "${HOME:-/tmp}"] {
            XCTAssertEqual(ControlMasking.argument(text), text)
        }
        try assertHidden(["hunter2"], in: local("node", ["s.js", "${DB_PASSWORD:-hunter2}", "${API_TOKEN:=hunter2}", "${X_KEY=hunter2}"]))
        try assertHidden(["hunter2"], in: local("sh", ["-c", "exec srv ${DB_PASSWORD:-hunter2}"]))
    }

    /// In a shell line, a backslash before a space joins the words a hidden value spans (M-1e), so
    /// the rest of it is hidden too; a backslash elsewhere is a Windows path's and reads as written.
    func testABackslashEscapedSpaceInAHiddenValueHidesTheRest() throws {
        XCTAssertEqual(ControlMasking.argument("srv --api-key hunter\\ two"), "srv --api-key ••• •••")
        XCTAssertEqual(ControlMasking.argument("srv --api-key a\\ b\\ c --port 80"), "srv --api-key ••• ••• ••• --port 80")
        XCTAssertEqual(ControlMasking.argument("dir C:\\ /s"), "dir C:\\ /s")
        try assertHidden(["two"], in: local("sh", ["-c", "srv --api-key hunter\\ two"]))
    }

    /// Known gap (M-1f, over-masking, not a leak): `API_KEY=x npx s` in one argument is masked past
    /// its first word, since it cannot be told from `API_KEY=my secret value`, which must stay hidden whole.
    func testAnAssignmentBeforeACommandInOneArgumentIsMaskedWhole() {
        XCTAssertEqual(ControlMasking.arguments(["-c", "API_KEY=x npx s"]), ["-c", "API_KEY=•••"])
    }

    /// `env` and `headers` in any case, and `envs`, hold secrets (M-1g), wherever they sit.
    func testMisCasedEnvironmentAndHeaderKeysAreMasked() throws {
        let config = JSONValue.object([
            "command": .string("x"), "Env": .object(["A": .string("hunter2")]), "envs": .object(["B": .string("hunter2")]),
            "HEADERS": .object(["C": .string("hunter2")]), "nested": .object(["Env": .object(["D": .string("hunter2")])]),
        ])
        XCTAssertEqual(ControlMasking.maskedConfig(config).value(at: JSONPointer(["Env", "A"])), .string("set"))
        try assertHidden(["hunter2"], in: config)
    }

    /// A run of scheme words after a flag named for a secret hides the word after it (M-a). Known
    /// gaps, each a config that does not work as written: a value after `--`, and a second word
    /// after the scheme word and its value, read as any argument does.
    func testARunOfSchemeWordsHidesTheValueAfterIt() throws {
        XCTAssertEqual(ControlMasking.arguments(["--auth", "Token", "Token", "hunter2"]), ["--auth", "•••", "•••", "•••"])
        try assertHidden(["hunter2"], in: local("sh", ["--auth", "Token", "Token", "hunter2"]))
        XCTAssertEqual(ControlMasking.arguments(["--password", "Basic", "--", "hunter2"]), ["--password", "•••", "--", "hunter2"])
        XCTAssertEqual(ControlMasking.arguments(["--auth", "Token", "abc", "def"]), ["--auth", "•••", "•••", "def"])
    }
}

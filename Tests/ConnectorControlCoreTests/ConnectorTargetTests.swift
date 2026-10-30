import XCTest
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/ConnectorTargetTests.cs.
/// What a connector's row, or an answer to Claude, says it runs: the allowlist that never shows a
/// secret.
final class ConnectorTargetTests: XCTestCase {
    /// A local connector running `command` with `args`.
    private func localConnector(_ command: String, _ args: [String] = []) -> MCPEntry {
        MCPEntry(config: .object(["command": .string(command), "args": .array(args.map(JSONValue.string))]))
    }

    /// The target a local connector shows, which must not carry `secret` whatever else it says.
    private func assertTarget(_ entry: MCPEntry, is expected: String, hides secret: String,
                              file: StaticString = #filePath, line: UInt = #line) {
        let target = ConnectorTarget.target(of: entry.config, home: "/Users/x")
        XCTAssertFalse(target.contains(secret), target, file: file, line: line)
        XCTAssertEqual(target, expected, file: file, line: line)
    }

    func testTargetShowsARemoteConnectorsHostOnly() {
        XCTAssertEqual(ConnectorTarget.target(of: RemotePattern.make(url: "https://u:p@api.githubcopilot.com/mcp?k=v"), home: "/Users/x"),
                       "api.githubcopilot.com")
        XCTAssertEqual(ConnectorTarget.target(of: RemotePattern.make(url: "http://localhost:8080/mcp"), home: "/Users/x"), "localhost:8080")
    }

    func testTargetShowsOnlyTheHostOfARemoteConnectorWithAHeader() {
        assertTarget(localConnector("npx", ["-y", "mcp-remote", "https://h.example/mcp", "--header", "Authorization: Bearer abc"]),
                     is: "h.example", hides: "abc")
    }

    /// A connector Claude reaches by URL, with no command at all, shows the URL's scheme and host
    /// through the same strict reading as a URL argument; one whose host is in doubt, or that has
    /// no URL to read, shows only that it is remote.
    func testTargetShowsAURLOnlyConnectorsSchemeAndHost() {
        let cases: [(JSONValue, String)] = [
            (.object(["url": .string("https://mcp.example.com/mcp")]), "https://mcp.example.com"),
            (.object(["type": .string("http"), "url": .string("https://u:SECRET@h.example:8443/mcp")]), "https://h.example:8443"),
            (.object(["url": .string("https://u:p@SECRET@h.example/mcp")]), "https://h.example"),
            (.object(["url": .string("https://u:p/SECRET@h.example/mcp")]), ConnectorTarget.remoteType),
            (.object(["url": .string("https://u:p?SECRET@h.example/mcp")]), ConnectorTarget.remoteType),
            (.object(["url": .string("https://u:p#SECRET@h.example/mcp")]), ConnectorTarget.remoteType),
            (.object(["url": .string("https://h.example/mcp?api_key=SECRET")]), "https://h.example"),
            (.object(["type": .string("sse"), "url": .string("https://h.example/sse"),
                      "headers": .object(["Authorization": .string("Bearer SECRET")])]), "https://h.example"),
            (.object(["type": .string("http"), "url": .string("h.example/SECRET")]), ConnectorTarget.remoteType),
            (.object(["url": .int(5)]), ConnectorTarget.remoteType),
            (.object(["type": .string("http")]), ConnectorTarget.remoteType),
        ]
        for (config, expected) in cases {
            let target = ConnectorTarget.target(of: config, home: "/Users/x")
            XCTAssertEqual(target, expected, "\(config)")
            XCTAssertFalse(target.contains("SECRET"), target)
        }
    }

    func testTargetShortensAScopedPackageAndTheHomeFolder() {
        XCTAssertEqual(ConnectorTarget.target(of: localConnector("npx", ["-y", "@modelcontextprotocol/server-filesystem", "/Users/x/Documents"]).config, home: "/Users/x"),
                       "npx …/server-filesystem ~/Documents")
    }

    /// A connector authored on Windows abbreviates its home folder the same way, whichever
    /// separator follows it and however its letters are cased.
    func testTargetShortensAWindowsHomeFolder() {
        XCTAssertEqual(ConnectorTarget.target(of: localConnector("node", [#"C:\Users\x\srv\index.js"#, "@scope/pkg@1.2", #"c:\users\X\a.js"#]).config,
                                               home: #"C:\Users\x"#),
                       #"node ~\srv\index.js …/pkg@1.2 ~\a.js"#)
    }

    func testTargetShowsAURLArgumentsSchemeAndHostOnly() {
        assertTarget(localConnector("tool", ["https://me:pw@h.example:8443/x?token=t#frag"]), is: "tool https://h.example:8443", hides: "pw")
    }

    func testTargetLeavesOutASlackWebhooksPath() {
        assertTarget(localConnector("tool", ["https://hooks.slack.com/services/T000/B000/XXXXsecret"]), is: "tool https://hooks.slack.com", hides: "XXXXsecret")
    }

    func testTargetLeavesOutFlagsAndTheirValues() {
        assertTarget(localConnector("/usr/local/bin/tool", ["--api-key", "abc", "--port", "80", "--token=abc"]), is: "tool", hides: "abc")
    }

    func testTargetLeavesOutAHeaderFlag() {
        assertTarget(localConnector("tool", ["-H", "X-Api-Key: abc", "https://h.example/x"]), is: "tool https://h.example", hides: "abc")
    }

    func testTargetLeavesOutAnEnvironmentAssignment() {
        assertTarget(localConnector("docker", ["run", "-i", "--rm", "-e", "GITHUB_TOKEN=abc", "ghcr.io/github/github-mcp-server"]),
                     is: "docker ghcr.io/github/github-mcp-server", hides: "abc")
    }

    func testTargetLeavesOutAShellString() {
        assertTarget(localConnector("sh", ["-c", "TOKEN=abc node srv.js"]), is: "sh", hides: "abc")
        assertTarget(localConnector("sh", ["-c", "curl -H 'Authorization: Bearer abc' https://h.example/x"]), is: "sh", hides: "abc")
    }

    func testTargetLeavesOutAnAttachedShortFlagValue() {
        assertTarget(localConnector("mysql-mcp", ["-pSECRET"]), is: "mysql-mcp", hides: "SECRET")
    }

    func testTargetLeavesOutAShortPositionalSecret() {
        assertTarget(localConnector("tool", ["hunter2"]), is: "tool", hides: "hunter2")
    }

    func testTargetLeavesOutAnUnprefixedKey() {
        assertTarget(localConnector("tool", ["sk_live_abc123"]), is: "tool", hides: "sk_live")
    }

    func testTargetLeavesOutAConnectionString() {
        assertTarget(localConnector("tool", ["Server=h;Password=x"]), is: "tool", hides: "Password=x")
    }

    func testTargetLeavesOutInlineJSON() {
        assertTarget(localConnector("tool", ["--config", #"{"apiKey":"abc"}"#]), is: "tool", hides: "abc")
    }

    /// The value is one the allowlist would show, so only the flag rule keeps it out; the flag's
    /// name is matched whatever its case.
    func testTargetLeavesOutTheValueOfASecretNamedFlagWhateverItsCase() {
        assertTarget(localConnector("tool", ["--token", "x.y"]), is: "tool", hides: "x.y")
        assertTarget(localConnector("tool", ["--TOKEN", "abc.def"]), is: "tool", hides: "abc.def")
    }

    /// A command line written as one string is split into launcher and arguments and each word
    /// held to the rule, so its last word - often a flag's value - is never shown for the launcher.
    func testTargetSplitsACommandLineWrittenAsOneString() {
        assertTarget(localConnector("cmd", ["/c", "npx -y @acme/server --api-key hunter2"]), is: "npx …/server", hides: "hunter2")
        assertTarget(MCPEntry(config: .object(["command": .string("npx -y server --token hunter2")])), is: "npx", hides: "hunter2")
        assertTarget(MCPEntry(config: .object(["command": .string("tool --token a.b")])), is: "tool", hides: "a.b")
        let quoted = #"npx -y mcp-remote https://h.example/mcp --header "Authorization: Bearer abc""#
        assertTarget(localConnector("cmd", ["/c", quoted]), is: "h.example", hides: "abc")
        XCTAssertFalse(ConnectorTarget.target(of: localConnector("cmd", ["/c", quoted]).config, home: "/Users/x").contains("Bearer"))
    }

    /// A quoted argument in a one-string command line - a header value, some JSON - is one
    /// argument, and one holding whitespace fails every shape the column shows; an unterminated
    /// quote runs to the end as part of its word.
    func testTargetLeavesOutAQuotedArgumentInACommandLine() {
        assertTarget(localConnector("cmd", ["/c", #"tool --header "X-Key: abc.def extra""#]), is: "tool", hides: "abc.def")
        assertTarget(localConnector("cmd", ["/c", #"npx -y @acme/server --config '{"k":"v.w"}'"#]), is: "npx …/server", hides: "v.w")
        assertTarget(MCPEntry(config: .object(["command": .string(#"tool "abc.def extra"#)])), is: "tool", hides: "abc.def")
        assertTarget(MCPEntry(config: .object(["command": .string(#""hunter.2 x" srv.js"#)])), is: "srv.js", hides: "hunter")
    }

    /// A command line is tokenized the way a shell passes it, so a quoted or partly quoted flag
    /// is still a flag, its value still drops, and only the first argument is the launcher.
    func testTargetTokenizesACommandLineLikeAShell() {
        assertTarget(MCPEntry(config: .object(["command": .string(#""C:\Program Files\Tool\tool.exe" --api-key hunter.2x"#)])),
                     is: "tool", hides: "hunter.2x")
        assertTarget(MCPEntry(config: .object(["command": .string(#"tool "--token" hunter.2x"#)])), is: "tool", hides: "hunter.2x")
        assertTarget(MCPEntry(config: .object(["command": .string(#"tool --to"ken" hunter.2x"#)])), is: "tool", hides: "hunter.2x")
        assertTarget(MCPEntry(config: .object(["command": .string(#"tool "a b" x.y"#)])), is: "tool x.y", hides: "a b")
        // An escaped quote does not close the argument, so `c.d` stays inside it.
        assertTarget(MCPEntry(config: .object(["command": .string(#"tool "a\"b c.d""#)])), is: "tool", hides: "c.d")
    }

    /// A command that is a path is never split, even with a space in it; one whose last component
    /// holds a space had arguments packed into it, and names no launcher.
    func testTargetNamesALauncherUnderProgramFiles() {
        assertTarget(localConnector(#"C:\Program Files\nodejs\node.exe"#, ["index.js"]), is: "node index.js", hides: "Program")
        assertTarget(localConnector(#""C:\Program Files\nodejs\node.exe""#, ["index.js"]), is: "node index.js", hides: "Program")
        assertTarget(localConnector(#"C:\Program Files\nodejs\npx.cmd"#, ["-y", "mcp-remote", "https://h.example/mcp"]), is: "h.example", hides: "npx")
        assertTarget(localConnector("/opt/bin/tool --password hunter.2x", ["srv.js"]), is: "srv.js", hides: "hunter.2x")
        assertTarget(localConnector(#"C:\Program Files (x86)\Tool\tool.exe"#, ["index.js"]), is: "tool index.js", hides: "Program")
        // Plain words packed after a Unix path do not cost it its launcher.
        assertTarget(localConnector("/usr/bin/tool srv"), is: "tool", hides: "srv")
    }

    /// A path command with a flag, a URL or a switch packed into it names no launcher: its last
    /// component could be the tail of an argument.
    func testTargetNamesNoLauncherForAPathCommandWithAPackedArgument() {
        assertTarget(localConnector("cmd", ["/c", #"C:\tools\notify.exe https://hooks.slack.com/services/T000/B000/XXXXsecret"#]),
                     is: "", hides: "XXXXsecret")
        assertTarget(localConnector("/usr/local/bin/mcp --api-key abc/hunter.2x"), is: "", hides: "hunter.2x")
        assertTarget(localConnector(#"C:\x\tool.exe --token ab\cd.ef"#), is: "", hides: "cd.ef")
    }

    /// A flag named for a secret at the end of a command guards the first argument after it,
    /// whether the command is a path or a tokenized line.
    func testTargetLeavesOutTheFirstArgumentAfterASecretNamedFlagInTheCommand() {
        assertTarget(localConnector("/opt/bin/tool --password", ["hunter.2x"]), is: "", hides: "hunter.2x")
        assertTarget(localConnector("tool --token", ["abc.def"]), is: "tool", hides: "abc.def")
    }

    /// A quote packed into a path command costs it its launcher, and a quoted or partly quoted
    /// flag at its end is still a flag named for a secret.
    func testTargetReadsAQuotedFlagPackedIntoAPathCommand() {
        assertTarget(localConnector(#"/opt/bin/tool "--password""#, ["hunter.2x"]), is: "", hides: "hunter.2x")
        assertTarget(localConnector(#"C:\x\tool.exe "--token" ab\cd.ef"#), is: "", hides: "cd.ef")
        assertTarget(localConnector(#"/opt/bin/tool --to"ken""#, ["hunter.2x"]), is: "", hides: "hunter.2x")
    }

    /// Each check that keeps a path command plain is load-bearing: a Windows switch and an
    /// assignment packed in both cost it its launcher.
    func testTargetNamesNoLauncherForAPathCommandWithASwitchOrAnAssignment() {
        assertTarget(localConnector(#"C:\x\tool.exe /key ab\cd.ef"#), is: "", hides: "cd.ef")
        assertTarget(localConnector("/opt/bin/tool key=ab/cd.ef"), is: "", hides: "cd.ef")
    }

    /// A password holding an unencoded `/`, `?` or `#` ends the authority early; what is left of
    /// the userinfo is refused as a host rather than shown, and so is a scheme that is not one.
    func testTargetRefusesAURLWhoseUserinfoHoldsADelimiter() {
        assertTarget(localConnector("tool", ["postgres://admin:hunter2#x@db.local/app"]), is: "tool", hides: "hunter2")
        assertTarget(localConnector("tool", ["https://apikey:sk_live_abc/x@api.example.com"]), is: "tool", hides: "sk_live")
        assertTarget(localConnector("tool", ["https://sk_live_abc/x@h"]), is: "tool", hides: "sk_live")
        assertTarget(localConnector("tool", ["sk-proj-abc123://x"]), is: "tool", hides: "abc123")
        assertTarget(localConnector("tool", ["mongodb+srv://u:p@cluster.example.net/db"]), is: "tool mongodb+srv://cluster.example.net", hides: "u:p")
        // The remote decoder accepts this URL, and its host is still checked.
        assertTarget(localConnector("npx", ["-y", "mcp-remote", "https://token123/x@h.example/mcp"]), is: ConnectorTarget.remoteType, hides: "token123")
    }

    /// A long random token is left out even with a `/` in it, which `looksLikeCredential` would
    /// not consider; paths and names with a dot, a hyphen or no digits are kept.
    func testTargetLeavesOutARandomTokenEvenWithASlash() {
        assertTarget(localConnector("tool", ["wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"]), is: "tool", hides: "wJalr")
        XCTAssertEqual(ConnectorTarget.target(of: localConnector("tool", ["/Users/x/Documents", "ghcr.io/github/github-mcp-server", "./build/v2Server"]).config,
                                               home: "/Users/x"),
                       "tool ~/Documents ghcr.io/github/github-mcp-server ./build/v2Server")
    }

    /// A Windows switch's value is not a path: a colon anywhere but a drive letter's drops it.
    func testTargetLeavesOutAWindowsSwitchesValue() {
        assertTarget(localConnector("tool", ["/p:Hunter2", "/token:abc", "/x:secret"]), is: "tool", hides: "secret")
        assertTarget(localConnector("tool", [#"C:\Users\x\Docs"#]), is: #"tool C:\Users\x\Docs"#, hides: "secret")
    }

    /// A secret shaped like a package still vanishes when a flag named for a secret precedes it.
    func testTargetLeavesOutWhateverFollowsASecretNamedFlag() {
        assertTarget(localConnector("tool", ["--password", "s3cr3t-pass", "--pass", "./x.key", "index.js"]), is: "tool index.js", hides: "s3cr3t")
    }

    func testTargetLeavesOutAPathCarryingAnAssignment() {
        assertTarget(localConnector("tool", ["/usr/bin/env TOKEN=abc"]), is: "tool", hides: "abc")
    }

    func testTargetLeavesOutACredentialShapedArtefact() {
        assertTarget(localConnector("tool", ["sk-abc.def", "ghp_0123456789abcdef0123456789abcdef.js"]), is: "tool", hides: "abc")
    }

    func testTargetShowsTheServerAPackageRunnerNames() {
        XCTAssertEqual(ConnectorTarget.target(of: localConnector("uvx", ["mcp-server-fetch"]).config, home: "/Users/x"), "uvx mcp-server-fetch")
        XCTAssertEqual(ConnectorTarget.target(of: localConnector("python", ["mcp_server.py"]).config, home: "/Users/x"), "python mcp_server.py")
    }

    /// A bare hyphenated word anywhere but the server slot is as likely a password as a package.
    func testTargetLeavesOutABareHyphenatedWordOutsideTheServerSlot() {
        assertTarget(localConnector("tool", ["hunter-2"]), is: "tool", hides: "hunter-2")
        assertTarget(localConnector("tool", ["-p", "s3cr3t-pass"]), is: "tool", hides: "s3cr3t")
        assertTarget(localConnector("tool", ["correct-horse-battery-staple"]), is: "tool", hides: "horse")
        assertTarget(localConnector("uvx", ["--from", "x", "my-server", "extra-word"]), is: "uvx", hides: "extra-word")
    }

    func testTargetLeavesOutThePwFlagsValue() {
        assertTarget(localConnector("tool", ["--pw", "a.b"]), is: "tool", hides: "a.b")
    }

    /// The server slot is the first positional argument, whatever it holds: a bare word there is
    /// shown because it cannot be told from a package name, and only there.
    func testTargetTrustsOnlyTheFirstPositionalAfterAPackageRunner() {
        XCTAssertEqual(ConnectorTarget.target(of: localConnector("npx", ["-y", "hunter-2", "@scope/pkg", "other-word"]).config, home: "/Users/x"),
                       "npx hunter-2 …/pkg")
    }

    /// Claude Desktop on Windows runs a server through `cmd /c`: what cmd runs is the launcher,
    /// named without its `.cmd`, and a bridge spelled that way is still a remote connector.
    func testTargetUnwrapsCmdAndAWindowsLaunchersExtension() {
        XCTAssertEqual(ConnectorTarget.target(of: localConnector("cmd", ["/c", "npx", "-y", "@modelcontextprotocol/server-filesystem", #"C:\Users\x\Docs"#]).config,
                                               home: #"C:\Users\x"#),
                       #"npx …/server-filesystem ~\Docs"#)
        XCTAssertEqual(ConnectorTarget.target(of: localConnector("npx.cmd", ["-y", "mcp-server-fetch"]).config, home: "/Users/x"), "npx mcp-server-fetch")
        assertTarget(localConnector("cmd", ["/c", "tool", "--token", "abc"]), is: "tool", hides: "abc")
        assertTarget(localConnector("CMD.EXE", ["/K", "npx", "-y", "mcp-remote", "https://h.example/mcp", "--header", "Authorization: Bearer abc"]),
                     is: "h.example", hides: "abc")
    }

    /// A launcher that could itself be a secret is left out, and the arguments still show; a
    /// command line's words after its first are arguments, held to the rule like any other.
    func testTargetLeavesOutALauncherThatCouldBeASecret() {
        assertTarget(localConnector("TOKEN=abc node", ["srv.js"]), is: "srv.js", hides: "abc")
        assertTarget(localConnector("ghp_0123456789abcdef0123456789abcdef", ["srv.js"]), is: "srv.js", hides: "ghp_")
    }
}

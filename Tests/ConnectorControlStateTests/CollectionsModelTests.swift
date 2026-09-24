import Combine
import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/CollectionsModelTests.cs. The two panes of the
/// Collections window: the collections as items, the selected one's connectors as rows, the
/// toolbar's enablement, and the actions that go through the dialog seam.
@MainActor
final class CollectionsModelTests: XCTestCase {
    private func synced(fileName: String) -> CollectionsFile.Entry {
        CollectionsFile.Entry(kind: .synced, fileName: fileName)
    }

    private func published(slug: String) -> CollectionsFile.Entry {
        CollectionsFile.Entry(kind: .local, publish: CollectionsFile.PublishRecord(slug: slug, origin: "origin", intent: .none))
    }

    private func bound(_ path: String?) -> CollectionsLocalCache.SyncedBinding {
        CollectionsLocalCache.SyncedBinding(path: path, lastHash: nil, excluded: [:])
    }

    /// Writes both collection files where the app reads them, then reloads so the state picks
    /// them up — the shape a subscribe or a publish would leave behind.
    private func seed(_ h: AppStateHarness, _ state: AppState,
                      file: CollectionsFile, cache: CollectionsLocalCache? = nil) throws {
        try file.save(to: h.storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        try (cache ?? CollectionsLocalCache(synced: [:], published: [:]))
            .save(to: state.service.paths.collectionsCacheURL, staging: nil)
        state.reload()
    }

    private func local(_ command: String, _ args: [String] = []) -> MCPEntry {
        MCPEntry(config: .object(["command": .string(command), "args": .array(args.map(JSONValue.string))]))
    }

    /// Leaves a refusal in `lastError` without moving the window: the store refuses an empty name
    /// before it renames anything.
    private func presetError(_ model: CollectionsModel, _ h: AppStateHarness) {
        h.dialogs.nextPromptAnswer = ""
        model.rename()
        XCTAssertNotNil(model.lastError)
    }

    // MARK: - Items

    func testItemsMirrorTheStoreAndMarkSyncedPublishedAndPending() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Shared"))
        XCTAssertNil(state.createCollection(named: "Team"))
        state.switchCollection(to: "Default")
        let file = CollectionsFile(collections: ["Shared": published(slug: "shared"), "Team": synced(fileName: "team.json")])
        let cache = CollectionsLocalCache(synced: ["Team": bound("/shared/team.json")],
                                          published: ["Shared": .init(folder: "/tmp/share", lastWrittenHash: nil)])
        try seed(h, state, file: file, cache: cache)

        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        XCTAssertEqual(model.items.map(\.name), ["Default", "Shared", "Team"])   // the chip menu's order
        XCTAssertEqual(model.items.map(\.id), ["Default", "Shared", "Team"])
        XCTAssertEqual(model.items.map(\.kind), [.local, .local, .synced])
        XCTAssertEqual(model.items.map(\.isActive), [true, false, false])
        XCTAssertEqual(model.items.map(\.hasPendingUpdate), [false, false, false])
        XCTAssertEqual(model.items.map(\.isLocated), [true, true, true], "a local collection has no file to find")

        // The republish is what repaints the view, and it is the one line a passthrough cannot prove.
        var repaints = 0
        let sink = model.objectWillChange.sink { _ in repaints += 1 }
        defer { sink.cancel() }
        state.pendingUpdates = ["Team": CollectionDiff(added: ["jira"], removed: [], changed: [])]
        XCTAssertEqual(model.items.map(\.hasPendingUpdate), [false, false, true])
        XCTAssertGreaterThan(repaints, 0)

        // The binding gone, the sidecar still names the file: the item says it is not located.
        try seed(h, state, file: file, cache: CollectionsLocalCache(synced: [:], published: cache.published))
        XCTAssertEqual(model.items.map(\.isLocated), [true, true, false])
        XCTAssertFalse(state.isLocated("Team"))
        XCTAssertTrue(state.isLocated("Default"))

        // dispose() cuts the republish: the items still read through, nothing repaints.
        model.dispose()
        let before = repaints
        state.pendingUpdates = [:]
        XCTAssertEqual(model.items.map(\.hasPendingUpdate), [false, false, false])
        XCTAssertEqual(repaints, before)
    }

    // MARK: - Rows

    /// Rows list in UTF-16 code-unit order, as Windows lists them: a character beyond U+FFFF comes
    /// before U+FF5E there, where Swift's own `<` puts it after.
    func testRowsSortAsWindowsDoes() throws {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        XCTAssertNil(state.upsert(name: "\u{FF5E}", entry: local("uvx"), renamedFrom: nil))
        XCTAssertNil(state.upsert(name: "\u{1F600}", entry: local("uvx"), renamedFrom: nil))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        XCTAssertEqual(model.rows.map(\.name), ["\u{1F600}", "\u{FF5E}"])
        model.setChecked("\u{FF5E}", true)
        model.setChecked("\u{1F600}", true)
        XCTAssertNil(state.createCollection(named: "Other"))   // a copy of the active one: both clash
        state.switchCollection(to: "Default")
        XCTAssertEqual(model.checkedNamesClashing(in: "Other"), ["\u{1F600}", "\u{FF5E}"])
    }

    func testRowsForASyncedCollectionAreLockedAndUncheckable() throws {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        XCTAssertNil(state.upsert(name: "github", entry: MCPEntry(config: AppStateHarness.remote("https://github.example/mcp")),
                                  renamedFrom: nil, in: "Team"))
        XCTAssertNil(state.upsert(name: "Ledger", entry: local("/usr/local/bin/node", ["index.js"]), renamedFrom: nil, in: "Team"))
        XCTAssertNil(state.upsert(name: "jira", entry: MCPEntry(config: .object([
            "command": .string("npx"),
            "env": .object(["JIRA_TOKEN": .string(Placeholder.marker("JIRA_TOKEN"))]),
        ])), renamedFrom: nil, in: "Team"))
        XCTAssertNil(state.upsert(name: "notes", entry: local("uvx"), renamedFrom: nil, in: "Default"))
        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]),
                 cache: CollectionsLocalCache(synced: ["Team": bound("/shared/team.json")], published: [:]))

        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Team"
        // Uppercase first: ordinal, the order the popover lists the same connectors in.
        XCTAssertEqual(model.rows.map(\.name), ["Ledger", "github", "jira"])
        XCTAssertEqual(model.rows.map(\.id), ["Ledger", "github", "jira"])
        XCTAssertEqual(model.rows.map(\.target), ["node index.js", "github.example", "npx"])
        XCTAssertTrue(model.rows.allSatisfy(\.isLocked), "every row of a synced collection carries the lock")
        XCTAssertEqual(model.rows.map(\.caution), [nil, nil, AppState.needsValueCaution("JIRA_TOKEN")])

        // Nothing in a synced collection can be exported, so nothing in one can be ticked.
        model.setChecked("github", true)
        XCTAssertTrue(model.rows.allSatisfy { !$0.checked })
        XCTAssertEqual(model.checkedNames, [])
        XCTAssertEqual(model.exportIntentForChecked(), [])

        // The same rows in a local collection do tick, and the ticks belong to that collection.
        model.selected = "Default"
        XCTAssertEqual(model.rows.map(\.name), ["notes"])
        XCTAssertEqual(model.rows.map(\.target), ["uvx"])
        XCTAssertTrue(model.rows.allSatisfy { !$0.isLocked })
        model.setChecked("notes", true)
        XCTAssertEqual(model.rows.map(\.checked), [true])
        XCTAssertEqual(model.checkedNames, ["notes"])
        XCTAssertEqual(model.exportIntentForChecked(), ["notes"])
        model.setChecked("notes", false)
        XCTAssertEqual(model.exportIntentForChecked(), [])

        // The pencil opens the row in the collection the window is showing, not the active one.
        model.selected = "Team"
        let target = model.editTarget(for: "jira")
        XCTAssertEqual(target.collection, "Team")
        XCTAssertEqual(target.name, "jira")
        XCTAssertFalse(target.isNew)
        XCTAssertEqual(target.entry.config, state.store.collections["Team"]?.mcps["jira"]?.config)

        // One connector list must not appear in two orders: the window lists the active
        // collection's rows exactly as the popover does.
        state.switchCollection(to: "Team")
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(model.rows.map(\.name), popover.rows.map(\.name))
    }

    // MARK: - Target column

    /// The target a local connector shows, which must not carry `secret` whatever else it says.
    private func assertTarget(_ entry: MCPEntry, is expected: String, hides secret: String,
                              file: StaticString = #filePath, line: UInt = #line) {
        let target = CollectionsModel.target(of: entry.config, home: "/Users/x")
        XCTAssertFalse(target.contains(secret), target, file: file, line: line)
        XCTAssertEqual(target, expected, file: file, line: line)
    }

    func testTargetShowsARemoteConnectorsHostOnly() {
        XCTAssertEqual(CollectionsModel.target(of: RemotePattern.make(url: "https://u:p@api.githubcopilot.com/mcp?k=v"), home: "/Users/x"),
                       "api.githubcopilot.com")
        XCTAssertEqual(CollectionsModel.target(of: RemotePattern.make(url: "http://localhost:8080/mcp"), home: "/Users/x"), "localhost:8080")
    }

    func testTargetShowsOnlyTheHostOfARemoteConnectorWithAHeader() {
        assertTarget(local("npx", ["-y", "mcp-remote", "https://h.example/mcp", "--header", "Authorization: Bearer abc"]),
                     is: "h.example", hides: "abc")
    }

    func testTargetShortensAScopedPackageAndTheHomeFolder() {
        XCTAssertEqual(CollectionsModel.target(of: local("npx", ["-y", "@modelcontextprotocol/server-filesystem", "/Users/x/Documents"]).config, home: "/Users/x"),
                       "npx …/server-filesystem ~/Documents")
    }

    /// A connector authored on Windows abbreviates its home folder the same way, whichever
    /// separator follows it and however its letters are cased.
    func testTargetShortensAWindowsHomeFolder() {
        XCTAssertEqual(CollectionsModel.target(of: local("node", [#"C:\Users\x\srv\index.js"#, "@scope/pkg@1.2", #"c:\users\X\a.js"#]).config,
                                               home: #"C:\Users\x"#),
                       #"node ~\srv\index.js …/pkg@1.2 ~\a.js"#)
    }

    func testTargetShowsAURLArgumentsSchemeAndHostOnly() {
        assertTarget(local("tool", ["https://me:pw@h.example:8443/x?token=t#frag"]), is: "tool https://h.example:8443", hides: "pw")
    }

    func testTargetLeavesOutASlackWebhooksPath() {
        assertTarget(local("tool", ["https://hooks.slack.com/services/T000/B000/XXXXsecret"]), is: "tool https://hooks.slack.com", hides: "XXXXsecret")
    }

    func testTargetLeavesOutFlagsAndTheirValues() {
        assertTarget(local("/usr/local/bin/tool", ["--api-key", "abc", "--port", "80", "--token=abc"]), is: "tool", hides: "abc")
    }

    func testTargetLeavesOutAHeaderFlag() {
        assertTarget(local("tool", ["-H", "X-Api-Key: abc", "https://h.example/x"]), is: "tool https://h.example", hides: "abc")
    }

    func testTargetLeavesOutAnEnvironmentAssignment() {
        assertTarget(local("docker", ["run", "-i", "--rm", "-e", "GITHUB_TOKEN=abc", "ghcr.io/github/github-mcp-server"]),
                     is: "docker ghcr.io/github/github-mcp-server", hides: "abc")
    }

    func testTargetLeavesOutAShellString() {
        assertTarget(local("sh", ["-c", "TOKEN=abc node srv.js"]), is: "sh", hides: "abc")
        assertTarget(local("sh", ["-c", "curl -H 'Authorization: Bearer abc' https://h.example/x"]), is: "sh", hides: "abc")
    }

    func testTargetLeavesOutAnAttachedShortFlagValue() {
        assertTarget(local("mysql-mcp", ["-pSECRET"]), is: "mysql-mcp", hides: "SECRET")
    }

    func testTargetLeavesOutAShortPositionalSecret() {
        assertTarget(local("tool", ["hunter2"]), is: "tool", hides: "hunter2")
    }

    func testTargetLeavesOutAnUnprefixedKey() {
        assertTarget(local("tool", ["sk_live_abc123"]), is: "tool", hides: "sk_live")
    }

    func testTargetLeavesOutAConnectionString() {
        assertTarget(local("tool", ["Server=h;Password=x"]), is: "tool", hides: "Password=x")
    }

    func testTargetLeavesOutInlineJSON() {
        assertTarget(local("tool", ["--config", #"{"apiKey":"abc"}"#]), is: "tool", hides: "abc")
    }

    /// The value is one the allowlist would show, so only the flag rule keeps it out; the flag's
    /// name is matched whatever its case.
    func testTargetLeavesOutTheValueOfASecretNamedFlagWhateverItsCase() {
        assertTarget(local("tool", ["--token", "x.y"]), is: "tool", hides: "x.y")
        assertTarget(local("tool", ["--TOKEN", "abc.def"]), is: "tool", hides: "abc.def")
    }

    /// A command line written as one string is split into launcher and arguments and each word
    /// held to the rule, so its last word — often a flag's value — is never shown for the launcher.
    func testTargetSplitsACommandLineWrittenAsOneString() {
        assertTarget(local("cmd", ["/c", "npx -y @acme/server --api-key hunter2"]), is: "npx …/server", hides: "hunter2")
        assertTarget(MCPEntry(config: .object(["command": .string("npx -y server --token hunter2")])), is: "npx", hides: "hunter2")
        assertTarget(MCPEntry(config: .object(["command": .string("tool --token a.b")])), is: "tool", hides: "a.b")
        let quoted = #"npx -y mcp-remote https://h.example/mcp --header "Authorization: Bearer abc""#
        assertTarget(local("cmd", ["/c", quoted]), is: "h.example", hides: "abc")
        XCTAssertFalse(CollectionsModel.target(of: local("cmd", ["/c", quoted]).config, home: "/Users/x").contains("Bearer"))
    }

    /// A quoted argument in a one-string command line — a header value, some JSON — is one
    /// argument, and one holding whitespace fails every shape the column shows; an unterminated
    /// quote runs to the end as part of its word.
    func testTargetLeavesOutAQuotedArgumentInACommandLine() {
        assertTarget(local("cmd", ["/c", #"tool --header "X-Key: abc.def extra""#]), is: "tool", hides: "abc.def")
        assertTarget(local("cmd", ["/c", #"npx -y @acme/server --config '{"k":"v.w"}'"#]), is: "npx …/server", hides: "v.w")
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
        assertTarget(local(#"C:\Program Files\nodejs\node.exe"#, ["index.js"]), is: "node index.js", hides: "Program")
        assertTarget(local(#""C:\Program Files\nodejs\node.exe""#, ["index.js"]), is: "node index.js", hides: "Program")
        assertTarget(local(#"C:\Program Files\nodejs\npx.cmd"#, ["-y", "mcp-remote", "https://h.example/mcp"]), is: "h.example", hides: "npx")
        assertTarget(local("/opt/bin/tool --password hunter.2x", ["srv.js"]), is: "srv.js", hides: "hunter.2x")
        assertTarget(local(#"C:\Program Files (x86)\Tool\tool.exe"#, ["index.js"]), is: "tool index.js", hides: "Program")
        // Plain words packed after a Unix path do not cost it its launcher.
        assertTarget(local("/usr/bin/tool srv"), is: "tool", hides: "srv")
    }

    /// A path command with a flag, a URL or a switch packed into it names no launcher: its last
    /// component could be the tail of an argument.
    func testTargetNamesNoLauncherForAPathCommandWithAPackedArgument() {
        assertTarget(local("cmd", ["/c", #"C:\tools\notify.exe https://hooks.slack.com/services/T000/B000/XXXXsecret"#]),
                     is: "", hides: "XXXXsecret")
        assertTarget(local("/usr/local/bin/mcp --api-key abc/hunter.2x"), is: "", hides: "hunter.2x")
        assertTarget(local(#"C:\x\tool.exe --token ab\cd.ef"#), is: "", hides: "cd.ef")
    }

    /// A flag named for a secret at the end of a command guards the first argument after it,
    /// whether the command is a path or a tokenized line.
    func testTargetLeavesOutTheFirstArgumentAfterASecretNamedFlagInTheCommand() {
        assertTarget(local("/opt/bin/tool --password", ["hunter.2x"]), is: "", hides: "hunter.2x")
        assertTarget(local("tool --token", ["abc.def"]), is: "tool", hides: "abc.def")
    }

    /// A quote packed into a path command costs it its launcher, and a quoted or partly quoted
    /// flag at its end is still a flag named for a secret.
    func testTargetReadsAQuotedFlagPackedIntoAPathCommand() {
        assertTarget(local(#"/opt/bin/tool "--password""#, ["hunter.2x"]), is: "", hides: "hunter.2x")
        assertTarget(local(#"C:\x\tool.exe "--token" ab\cd.ef"#), is: "", hides: "cd.ef")
        assertTarget(local(#"/opt/bin/tool --to"ken""#, ["hunter.2x"]), is: "", hides: "hunter.2x")
    }

    /// Each check that keeps a path command plain is load-bearing: a Windows switch and an
    /// assignment packed in both cost it its launcher.
    func testTargetNamesNoLauncherForAPathCommandWithASwitchOrAnAssignment() {
        assertTarget(local(#"C:\x\tool.exe /key ab\cd.ef"#), is: "", hides: "cd.ef")
        assertTarget(local("/opt/bin/tool key=ab/cd.ef"), is: "", hides: "cd.ef")
    }

    /// A password holding an unencoded `/`, `?` or `#` ends the authority early; what is left of
    /// the userinfo is refused as a host rather than shown, and so is a scheme that is not one.
    func testTargetRefusesAURLWhoseUserinfoHoldsADelimiter() {
        assertTarget(local("tool", ["postgres://admin:hunter2#x@db.local/app"]), is: "tool", hides: "hunter2")
        assertTarget(local("tool", ["https://apikey:sk_live_abc/x@api.example.com"]), is: "tool", hides: "sk_live")
        assertTarget(local("tool", ["https://sk_live_abc/x@h"]), is: "tool", hides: "sk_live")
        assertTarget(local("tool", ["sk-proj-abc123://x"]), is: "tool", hides: "abc123")
        assertTarget(local("tool", ["mongodb+srv://u:p@cluster.example.net/db"]), is: "tool mongodb+srv://cluster.example.net", hides: "u:p")
        // The remote decoder accepts this URL, and its host is still checked.
        assertTarget(local("npx", ["-y", "mcp-remote", "https://token123/x@h.example/mcp"]), is: CollectionsModel.remoteType, hides: "token123")
    }

    /// A long random token is left out even with a `/` in it, which `looksLikeCredential` would
    /// not consider; paths and names with a dot, a hyphen or no digits are kept.
    func testTargetLeavesOutARandomTokenEvenWithASlash() {
        assertTarget(local("tool", ["wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"]), is: "tool", hides: "wJalr")
        XCTAssertEqual(CollectionsModel.target(of: local("tool", ["/Users/x/Documents", "ghcr.io/github/github-mcp-server", "./build/v2Server"]).config,
                                               home: "/Users/x"),
                       "tool ~/Documents ghcr.io/github/github-mcp-server ./build/v2Server")
    }

    /// A Windows switch's value is not a path: a colon anywhere but a drive letter's drops it.
    func testTargetLeavesOutAWindowsSwitchesValue() {
        assertTarget(local("tool", ["/p:Hunter2", "/token:abc", "/x:secret"]), is: "tool", hides: "secret")
        assertTarget(local("tool", [#"C:\Users\x\Docs"#]), is: #"tool C:\Users\x\Docs"#, hides: "secret")
    }

    /// A secret shaped like a package still vanishes when a flag named for a secret precedes it.
    func testTargetLeavesOutWhateverFollowsASecretNamedFlag() {
        assertTarget(local("tool", ["--password", "s3cr3t-pass", "--pass", "./x.key", "index.js"]), is: "tool index.js", hides: "s3cr3t")
    }

    func testTargetLeavesOutAPathCarryingAnAssignment() {
        assertTarget(local("tool", ["/usr/bin/env TOKEN=abc"]), is: "tool", hides: "abc")
    }

    func testTargetLeavesOutACredentialShapedArtefact() {
        assertTarget(local("tool", ["sk-abc.def", "ghp_0123456789abcdef0123456789abcdef.js"]), is: "tool", hides: "abc")
    }

    func testTargetShowsTheServerAPackageRunnerNames() {
        XCTAssertEqual(CollectionsModel.target(of: local("uvx", ["mcp-server-fetch"]).config, home: "/Users/x"), "uvx mcp-server-fetch")
        XCTAssertEqual(CollectionsModel.target(of: local("python", ["mcp_server.py"]).config, home: "/Users/x"), "python mcp_server.py")
    }

    /// A bare hyphenated word anywhere but the server slot is as likely a password as a package.
    func testTargetLeavesOutABareHyphenatedWordOutsideTheServerSlot() {
        assertTarget(local("tool", ["hunter-2"]), is: "tool", hides: "hunter-2")
        assertTarget(local("tool", ["-p", "s3cr3t-pass"]), is: "tool", hides: "s3cr3t")
        assertTarget(local("tool", ["correct-horse-battery-staple"]), is: "tool", hides: "horse")
        assertTarget(local("uvx", ["--from", "x", "my-server", "extra-word"]), is: "uvx", hides: "extra-word")
    }

    func testTargetLeavesOutThePwFlagsValue() {
        assertTarget(local("tool", ["--pw", "a.b"]), is: "tool", hides: "a.b")
    }

    /// The server slot is the first positional argument, whatever it holds: a bare word there is
    /// shown because it cannot be told from a package name, and only there.
    func testTargetTrustsOnlyTheFirstPositionalAfterAPackageRunner() {
        XCTAssertEqual(CollectionsModel.target(of: local("npx", ["-y", "hunter-2", "@scope/pkg", "other-word"]).config, home: "/Users/x"),
                       "npx hunter-2 …/pkg")
    }

    /// Claude Desktop on Windows runs a server through `cmd /c`: what cmd runs is the launcher,
    /// named without its `.cmd`, and a bridge spelled that way is still a remote connector.
    func testTargetUnwrapsCmdAndAWindowsLaunchersExtension() {
        XCTAssertEqual(CollectionsModel.target(of: local("cmd", ["/c", "npx", "-y", "@modelcontextprotocol/server-filesystem", #"C:\Users\x\Docs"#]).config,
                                               home: #"C:\Users\x"#),
                       #"npx …/server-filesystem ~\Docs"#)
        XCTAssertEqual(CollectionsModel.target(of: local("npx.cmd", ["-y", "mcp-server-fetch"]).config, home: "/Users/x"), "npx mcp-server-fetch")
        assertTarget(local("cmd", ["/c", "tool", "--token", "abc"]), is: "tool", hides: "abc")
        assertTarget(local("CMD.EXE", ["/K", "npx", "-y", "mcp-remote", "https://h.example/mcp", "--header", "Authorization: Bearer abc"]),
                     is: "h.example", hides: "abc")
    }

    /// A launcher that could itself be a secret is left out, and the arguments still show; a
    /// command line's words after its first are arguments, held to the rule like any other.
    func testTargetLeavesOutALauncherThatCouldBeASecret() {
        assertTarget(local("TOKEN=abc node", ["srv.js"]), is: "srv.js", hides: "abc")
        assertTarget(local("ghp_0123456789abcdef0123456789abcdef", ["srv.js"]), is: "srv.js", hides: "ghp_")
    }

    /// End to end through the rows: a connector carrying secrets five ways shows none of them.
    /// (The harness's seeded fixture holds no secrets, so this plants its own.)
    func testNoRowTargetCarriesASecret() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let secrets = ["ghp_0123456789abcdef0123456789abcdef", "s3cr3t-pass", "tok123", "hdrsecret", "kvsecret"]
        let entry = MCPEntry(config: .object([
            "command": .string("/opt/bin/tool"),
            "args": .array(["--password", secrets[1], "--token=\(secrets[2])", secrets[0],
                            "--header", "Authorization: Bearer \(secrets[3])", "-e", "DB_PASSWORD=\(secrets[4])"].map(JSONValue.string)),
            "env": .object(["API_KEY": .string("envsecret")]),
        ]))
        XCTAssertNil(state.upsert(name: "leaky", entry: entry, renamedFrom: nil, in: "Default"))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Default"
        let target = try XCTUnwrap(model.rows.first { $0.name == "leaky" }).target
        XCTAssertEqual(target, "tool")
        for secret in secrets + ["envsecret"] { XCTAssertFalse(target.contains(secret), secret) }
    }

    // MARK: - Toolbar

    func testToolbarEnablementFollowsTheSelection() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Shared"))
        XCTAssertNil(state.createCollection(named: "Team"))
        state.switchCollection(to: "Default")
        let file = CollectionsFile(collections: ["Shared": published(slug: "shared"), "Team": synced(fileName: "team.json")])
        let located = CollectionsLocalCache(synced: ["Team": bound("/shared/team.json")],
                                            published: ["Shared": .init(folder: "/tmp/share", lastWrittenHash: nil)])
        try seed(h, state, file: file, cache: located)

        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        XCTAssertEqual(model.selected, "Default", "the selection starts on the active collection")
        XCTAssertEqual(model.checkedNames, [], "nothing is ticked yet")
        model.setChecked("aws-mcp", true)
        XCTAssertEqual(model.checkedNames, ["aws-mcp"])
        XCTAssertFalse(model.canRefresh)
        XCTAssertTrue(model.canDelete)

        model.selected = "Shared"
        XCTAssertEqual(model.checkedNames, [], "the ticks belonged to the collection that was showing")
        XCTAssertFalse(model.canRefresh)
        XCTAssertTrue(model.canDelete)

        model.selected = "Team"
        XCTAssertEqual(model.checkedNames, [])
        XCTAssertTrue(model.canRefresh)
        XCTAssertTrue(model.canDelete, "a synced collection goes without taking the last local one with it")

        // Nothing to refresh until the file is found on this machine.
        try seed(h, state, file: file, cache: CollectionsLocalCache(synced: [:], published: located.published))
        XCTAssertFalse(model.canRefresh)

        // With the second local collection gone, the last one cannot be deleted.
        XCTAssertNil(state.deleteCollection(named: "Shared"))
        model.selected = "Default"
        XCTAssertFalse(model.canDelete)
        model.selected = "Team"
        XCTAssertTrue(model.canDelete)

        // Nor can the last collection of any kind: the store always has an active one, so a lone
        // synced collection is no more deletable than a lone local one.
        XCTAssertNil(state.deleteCollection(named: "Team"))
        try seed(h, state, file: CollectionsFile(collections: ["Default": synced(fileName: "default.json")]),
                 cache: CollectionsLocalCache(synced: ["Default": bound("/shared/default.json")], published: [:]))
        XCTAssertEqual(state.collectionNames, ["Default"])
        XCTAssertTrue(state.isSynced("Default"))
        XCTAssertFalse(model.canDelete)
    }

    // MARK: - Create, rename, delete

    /// A cancelled prompt, or a verb that finds nothing to act on, leaves no error behind: the
    /// window shows `lastError` after every verb, so one left over from an earlier failure would
    /// come back as if this verb had failed. A declined confirmation counts as a cancel: it clears
    /// the last error too, and changes nothing else.
    func testAVerbThatDoesNothingClearsTheLastError() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        XCTAssertNil(state.upsert(name: "alpha", entry: local("/bin/alpha"), renamedFrom: nil, in: "Default"))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Default"

        func leavesNoError(_ verb: String, _ action: () -> Void, line: UInt = #line) {
            presetError(model, h)
            h.dialogs.nextPromptAnswer = nil
            action()
            XCTAssertNil(model.lastError, "\(verb) brought the earlier error back", line: line)
        }

        // Cancelled prompts.
        leavesNoError("create") { model.create() }
        leavesNoError("rename") { model.rename() }
        leavesNoError("duplicate") { model.duplicate() }

        // Nothing to act on.
        leavesNoError("makeLocalCopy of a local collection") { model.makeLocalCopy() }
        leavesNoError("stopPublishing of a collection that does not publish") { model.stopPublishing() }
        leavesNoError("copyChecked with nothing ticked") { model.copyChecked(into: "Team") }
        leavesNoError("copyCheckedIntoNewCollection with nothing ticked") { model.copyCheckedIntoNewCollection() }
        leavesNoError("removeChecked with nothing ticked") { model.removeChecked() }

        model.setChecked("alpha", true)
        leavesNoError("copyChecked into a collection it does not offer") { model.copyChecked(into: "Default") }
        leavesNoError("copyCheckedIntoNewCollection, cancelled") { model.copyCheckedIntoNewCollection() }
        XCTAssertEqual(model.checkedNames, ["alpha"], "nothing was copied, so the ticks stay")

        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]))
        model.selected = "Team"
        leavesNoError("makeLocalCopy, cancelled") { model.makeLocalCopy() }
        XCTAssertEqual(state.collectionNames, ["Default", "Team"], "no prompt that was cancelled made anything")
    }

    func testCreateRenameDeleteGoThroughTheDialogs() throws {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Work"))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        XCTAssertEqual(model.selected, "Work")

        // A cancelled prompt does nothing at all.
        h.dialogs.nextPromptAnswer = nil
        model.create()
        XCTAssertEqual(h.dialogs.prompts.last, FakeDialogs.PromptCall(title: AppState.newCollectionTitle, initial: ""))
        XCTAssertEqual(state.collectionNames, ["Default", "Work"])
        XCTAssertNil(model.lastError)

        h.dialogs.nextPromptAnswer = "  Team  "
        model.create()
        XCTAssertEqual(state.collectionNames, ["Default", "Team", "Work"])
        XCTAssertEqual(model.selected, "Team", "the window shows what it just made")
        XCTAssertNil(model.lastError)

        // A name the store refuses comes back as the model's error.
        h.dialogs.nextPromptAnswer = "Work"
        model.create()
        XCTAssertNotNil(model.lastError)
        XCTAssertEqual(state.collectionNames, ["Default", "Team", "Work"])

        h.dialogs.nextPromptAnswer = "Team B"
        model.rename()
        XCTAssertEqual(h.dialogs.prompts.last, FakeDialogs.PromptCall(title: AppState.renameCollectionTitle, initial: "Team"))
        XCTAssertEqual(state.collectionNames, ["Default", "Team B", "Work"])
        XCTAssertEqual(model.selected, "Team B", "the selection follows the name it just gave")
        XCTAssertNil(model.lastError, "a successful action clears the last one's error")

        // Declined: the collection stays, and the earlier error does not come back.
        presetError(model, h)
        h.dialogs.nextConfirm = false
        model.delete()
        let asked = try XCTUnwrap(h.dialogs.confirms.last)
        XCTAssertEqual(asked.message, AppState.deleteCollectionMessage("Team B"))
        XCTAssertEqual(asked.primary, AppState.deleteButton)
        XCTAssertTrue(asked.destructive)
        XCTAssertEqual(state.collectionNames, ["Default", "Team B", "Work"])
        XCTAssertNil(model.lastError, "a declined confirmation clears the last error")

        h.dialogs.nextConfirm = true
        model.delete()
        XCTAssertEqual(state.collectionNames, ["Default", "Work"])
        XCTAssertEqual(h.dialogs.confirms.count, 2, "an unpublished collection is asked about once")
        XCTAssertEqual(model.selected, state.activeCollection)
    }

    func testStopSyncingConfirmsAndADeclineLeavesTheCollectionSynced() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        state.switchCollection(to: "Default")
        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]),
                 cache: CollectionsLocalCache(synced: ["Team": bound("/shared/team.json")], published: [:]))
        XCTAssertTrue(state.isSynced("Team"))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Team"

        // Declined: the collection is still synced, and the earlier error does not come back.
        // The reassurance lives in the question now that the button no longer carries it.
        presetError(model, h)
        h.dialogs.nextConfirm = false
        model.stopSyncing()
        let asked = try XCTUnwrap(h.dialogs.confirms.last)
        XCTAssertEqual(asked.message, CollectionsModel.stopSyncingMessage("Team"))
        XCTAssertEqual(asked.informative, CollectionsModel.stopSyncingInformative)
        XCTAssertEqual(asked.primary, CollectionsModel.stopSyncingAction)
        XCTAssertFalse(asked.destructive, "nothing is lost: every connector stays")
        XCTAssertTrue(state.isSynced("Team"))
        XCTAssertNil(model.lastError, "a declined confirmation clears the last error")

        h.dialogs.nextConfirm = true
        model.stopSyncing()
        XCTAssertFalse(state.isSynced("Team"))
        XCTAssertEqual(state.collectionNames, ["Default", "Team"], "the collection stays, now local")
        XCTAssertEqual(h.dialogs.confirms.count, 2)
    }

    /// Stop Syncing, Refresh and Duplicate guard their own preconditions, as Make Local Copy and
    /// Stop Publishing do: out of turn, each does nothing, asks nothing, and clears the last error.
    func testVerbsOutOfTurnDoNothingAndAskNothing() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        state.switchCollection(to: "Default")
        // Synced, but not located on this machine: nothing to refresh.
        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]))
        XCTAssertTrue(state.isSynced("Team"))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }

        model.selected = "Default"
        presetError(model, h)
        model.stopSyncing()
        XCTAssertEqual(h.dialogs.confirms, [], "a local collection has nothing to stop syncing")
        XCTAssertNil(model.lastError)

        model.selected = "Team"
        XCTAssertFalse(model.canRefresh)
        presetError(model, h)
        model.refresh()
        XCTAssertNil(state.sourceErrors["Team"], "no read was attempted")
        XCTAssertNil(model.lastError)

        presetError(model, h)
        let prompted = h.dialogs.prompts.count
        XCTAssertFalse(model.duplicate(), "a synced collection offers Make Local Copy instead")
        XCTAssertEqual(h.dialogs.prompts.count, prompted, "no name is asked for")
        XCTAssertEqual(state.collectionNames, ["Default", "Team"])
        XCTAssertNil(model.lastError)
    }

    func testDeletingAPublishedCollectionAsksAboutTheFile() throws {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        let folder = h.dir.file("share")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        XCTAssertNil(state.createCollection(named: "Shared"))
        XCTAssertNil(state.createCollection(named: "Consulting"))
        XCTAssertNil(state.startPublishing("Shared", to: folder.path, intent: .none))
        XCTAssertNil(state.startPublishing("Consulting", to: folder.path, intent: .none))
        let sharedFile = folder.appendingPathComponent("shared.json")
        let consultingFile = folder.appendingPathComponent("consulting.json")
        XCTAssertTrue(FileManager.default.fileExists(atPath: sharedFile.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: consultingFile.path))

        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Shared"
        h.dialogs.confirmAnswers = [true, false]   // delete the collection, keep the document
        model.delete()
        XCTAssertEqual(h.dialogs.confirms.map(\.message),
                       [AppState.deleteCollectionMessage("Shared"),
                        CollectionsModel.deletePublishedFileQuestion("shared.json")])
        let fileQuestion = try XCTUnwrap(h.dialogs.confirms.last)
        XCTAssertEqual(fileQuestion.primary, CollectionsModel.removeFileButton)
        XCTAssertEqual(fileQuestion.cancel, CollectionsModel.keepFileButton)
        XCTAssertFalse(fileQuestion.destructive)
        XCTAssertFalse(state.collectionNames.contains("Shared"))
        XCTAssertTrue(FileManager.default.fileExists(atPath: sharedFile.path),
                      "Keep leaves the copy the team reads where it is")

        // The same question on its own, answered the other way.
        model.selected = "Consulting"
        h.dialogs.confirmAnswers = [true]
        model.stopPublishing()
        XCTAssertEqual(h.dialogs.confirms.last?.message, CollectionsModel.deletePublishedFileQuestion("consulting.json"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: consultingFile.path))
        XCTAssertFalse(state.isPublished("Consulting"))
        XCTAssertTrue(state.collectionNames.contains("Consulting"), "Stop Publishing keeps the collection")
    }

    func testAFailedPublishIsStoppedWithoutAskingAboutTheFile() throws {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        let folder = h.dir.file("share")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        XCTAssertNil(state.createCollection(named: "Shared"))
        XCTAssertNil(state.startPublishing("Shared", to: folder.path, intent: .none))
        let file = folder.appendingPathComponent("shared.json")
        XCTAssertTrue(FileManager.default.fileExists(atPath: file.path))

        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Shared"
        // The folder that refused the write would refuse the delete, so Remove is not offered.
        state.publishError = CollectionPublishError(collection: "Shared", message: "the folder is read-only")
        model.stopPublishing()
        XCTAssertTrue(h.dialogs.confirms.isEmpty, "nothing to ask when Remove could not be honoured")
        XCTAssertFalse(state.isPublished("Shared"))
        XCTAssertTrue(FileManager.default.fileExists(atPath: file.path), "the document stays where it is")
        XCTAssertNil(state.publishError)

        // Another collection's failure is not this one's, so the question comes back.
        XCTAssertNil(state.createCollection(named: "Consulting"))
        XCTAssertNil(state.startPublishing("Consulting", to: folder.path, intent: .none))
        model.selected = "Consulting"
        state.publishError = CollectionPublishError(collection: "Shared", message: "the folder is read-only")
        h.dialogs.confirmAnswers = [false]
        model.stopPublishing()
        XCTAssertEqual(h.dialogs.confirms.map(\.message),
                       [CollectionsModel.deletePublishedFileQuestion("consulting.json")])
    }

    // MARK: - Toggles

    /// The window has no switch of its own any more; the toggle in a named collection is
    /// AppState's, kept here because nothing else pins it.
    func testSetEnabledInAnInactiveCollectionLeavesClaudesConfigAlone() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Work"))
        state.switchCollection(to: "Default")

        state.setEnabled("aws-mcp", false, in: "Work")
        XCTAssertEqual(state.store.collections["Work"]?.mcps["aws-mcp"]?.enabled, false)
        XCTAssertEqual(try h.storeOnDisk().collections["Work"]?.mcps["aws-mcp"]?.enabled, false)
        XCTAssertEqual(state.store.collections["Default"]?.mcps["aws-mcp"]?.enabled, true)
        XCTAssertNotNil(try h.claudeServers()["aws-mcp"], "Claude runs the active collection, which did not change")

        // The same toggle in the active collection does reach Claude.
        state.setEnabled("aws-mcp", false, in: "Default")
        XCTAssertNil(try h.claudeServers()["aws-mcp"])
    }

    // MARK: - Detail line

    func testDetailLineFollowsTheCollectionState() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Shared"))
        XCTAssertNil(state.createCollection(named: "Team"))
        state.switchCollection(to: "Default")
        let file = CollectionsFile(collections: ["Shared": published(slug: "shared"), "Team": synced(fileName: "team.json")])
        let located = CollectionsLocalCache(synced: ["Team": bound("/shared/team.json")],
                                            published: ["Shared": .init(folder: "/Acme/mcp", lastWrittenHash: nil)])
        try seed(h, state, file: file, cache: located)

        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        XCTAssertEqual(model.detailLine, CollectionsModel.localDetail(3) + CollectionsModel.activeSuffix)

        model.selected = "Shared"
        XCTAssertEqual(model.detailLine,
                       CollectionsModel.localDetail(3) + " · " + CollectionsModel.publishedDetail("/Acme/mcp"))

        model.selected = "Team"
        XCTAssertEqual(model.detailLine,
                       CollectionsModel.syncedDetail("/shared/team.json", CollectionsModel.upToDateStatus))
        state.pendingUpdates = ["Team": CollectionDiff(added: ["jira"], removed: [], changed: [])]
        XCTAssertEqual(model.detailLine,
                       CollectionsModel.syncedDetail("/shared/team.json", CollectionsModel.updateAvailableStatus))
        state.sourceErrors = ["Team": "team.json couldn’t be read"]
        XCTAssertEqual(model.detailLine,
                       CollectionsModel.syncedDetail("/shared/team.json", "team.json couldn’t be read"),
                       "what went wrong outranks what is waiting")

        // Not located: there is nothing to say about the file except that it is missing.
        try seed(h, state, file: file, cache: CollectionsLocalCache(synced: [:], published: located.published))
        XCTAssertEqual(model.detailLine, CollectionsModel.unlocatedDetail)
        XCTAssertFalse(model.canRefresh)

        // A synced entry that records no file name either — a hand-edited or foreign collections
        // file. Nothing asks to be located, but there is still no document to name or to read.
        try seed(h, state, file: CollectionsFile(collections: [
            "Shared": published(slug: "shared"), "Team": CollectionsFile.Entry(kind: .synced),
        ]), cache: CollectionsLocalCache(synced: [:], published: located.published))
        XCTAssertTrue(state.isLocated("Team"), "nothing is waiting to be pointed at")
        XCTAssertEqual(model.detailLine, CollectionsModel.unlocatedDetail)
        XCTAssertFalse(model.canRefresh, "Refresh would read a document nobody can point at")
    }

    // MARK: - Selection

    func testSelectionFallsBackToTheActiveCollectionWhenItsCollectionDisappears() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Work"))
        XCTAssertNil(state.createCollection(named: "Spare"))
        state.switchCollection(to: "Default")
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }

        model.selected = "Work"
        XCTAssertEqual(model.selected, "Work")
        model.setChecked("aws-mcp", true)
        XCTAssertEqual(model.checkedNames, ["aws-mcp"])

        XCTAssertNil(state.deleteCollection(named: "Work"))
        XCTAssertEqual(model.selected, "Default")
        XCTAssertEqual(model.selected, state.activeCollection)
        XCTAssertEqual(model.checkedNames, [], "the ticks belonged to the collection that is gone")

        // A rename anywhere else is the same disappearance: the name selected is no longer a collection.
        model.selected = "Spare"
        XCTAssertNil(state.renameCollection("Spare", to: "Spare Parts"))
        XCTAssertEqual(model.selected, state.activeCollection)

        // Switching the active collection from the window goes through AppState.
        model.switchTo("Spare Parts")
        XCTAssertEqual(state.activeCollection, "Spare Parts")
        XCTAssertEqual(model.items.first { $0.isActive }?.name, "Spare Parts")
    }

    // MARK: - Banner strip

    /// Default, active and published into a real folder; Team, synced with its file still to be
    /// found. The two banners the window can show therefore belong to different collections.
    private func twoBanners(_ h: AppStateHarness, _ state: AppState, publishingInto folder: URL) throws {
        XCTAssertNil(state.createCollection(named: "Team"))
        state.switchCollection(to: "Default")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try seed(h, state,
                 file: CollectionsFile(collections: ["Team": synced(fileName: "team.json"),
                                                     "Default": published(slug: "default")]),
                 cache: CollectionsLocalCache(
                     synced: [:], published: ["Default": .init(folder: folder.path, lastWrittenHash: nil)]))
    }

    func testTheBannerStripSpeaksOnlyForTheSelectedCollection() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let folder = h.dir.file("pub")
        try twoBanners(h, state, publishingInto: folder)
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }

        // Team's file is missing, but the window is showing Default: the strip says nothing.
        XCTAssertEqual(state.collectionBanner, .locate(collection: "Team", fileName: "team.json"))
        XCTAssertNil(model.bannerText)
        XCTAssertNil(model.bannerButton)
        XCTAssertFalse(model.bannerAction())

        model.selected = "Team"
        XCTAssertEqual(model.bannerText, AppState.collectionLocateBanner("Team"))
        XCTAssertEqual(model.bannerButton, PopoverModel.locateButton("team.json"))
        XCTAssertFalse(model.bannerAction(), "the view owes a file picker")

        // An update waiting is the one banner the strip can act on by itself.
        let diff = CollectionDiff(added: ["jira"], removed: [], changed: [])
        state.pendingUpdates = ["Team": diff]
        XCTAssertEqual(model.bannerText, AppState.collectionUpdateBanner("Team", diff.summary()))
        XCTAssertEqual(model.bannerButton, PopoverModel.reviewAndApplyButton)
        XCTAssertTrue(model.bannerAction())

        // A failed publish belongs to Default, so Team's strip goes quiet again.
        var repaints = 0
        let sink = model.objectWillChange.sink { _ in repaints += 1 }
        defer { sink.cancel() }
        state.publishError = CollectionPublishError(collection: "Default", message: "the folder is read-only")
        XCTAssertGreaterThan(repaints, 0, "a failed publish repaints the window")
        XCTAssertNil(model.bannerText)
        model.selected = "Default"
        XCTAssertEqual(model.bannerText,
                       AppState.collectionPublishFailedBanner("Default", folder.path, "the folder is read-only"))
        XCTAssertEqual(model.bannerButton, PopoverModel.chooseFolderButton)
        XCTAssertFalse(model.bannerAction(), "the view owes a folder picker")
    }

    func testTheBannerStripLocatesAndRepointsTheSelectedCollection() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        let first = h.dir.file("first")
        try twoBanners(h, state, publishingInto: first)
        let second = h.dir.file("second")
        try FileManager.default.createDirectory(at: second, withIntermediateDirectories: true)
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }

        // The window is showing Default, which the locate banner is not about.
        let document = h.dir.file("team.json")
        try CollectionDocumentSamples.dataTeam.serialized().write(to: document)
        XCTAssertNil(model.locateSource(document.path))
        XCTAssertNil(state.sourceBinding(of: "Team"))

        model.selected = "Team"
        XCTAssertNil(model.locateSource(document.path))
        XCTAssertEqual(state.sourceBinding(of: "Team")?.path, document.path)

        // The document found, the banner has moved on, so the publish forwarding stays out of it.
        XCTAssertNil(model.choosePublishFolder(second.path))
        XCTAssertEqual(state.collectionsCache.published["Default"]?.folder, first.path)

        // A failed publish belongs to Default, and only Default's window may answer it.
        state.publishError = CollectionPublishError(collection: "Default", message: "the folder is read-only")
        XCTAssertNil(model.choosePublishFolder(second.path), "Team is showing, not Default")
        XCTAssertEqual(state.collectionsCache.published["Default"]?.folder, first.path)

        model.selected = "Default"
        XCTAssertNil(model.choosePublishFolder(second.path))
        XCTAssertEqual(state.collectionsCache.published["Default"]?.folder, second.path)
        XCTAssertTrue(FileManager.default.fileExists(atPath: second.appendingPathComponent("default.json").path),
                      "the document lands in the folder just chosen")
    }
    func testTheWindowsGlyphsAndActionsCarryTheirOwnWords() {
        XCTAssertEqual(CollectionsModel.editTooltip, "Edit")
        XCTAssertEqual(CollectionsModel.makeActiveAction, "Make Active")
        XCTAssertEqual(CollectionsModel.lockedGlyphTooltip, "Read-only: synced from the collection's author")
    }

    func testTheSidebarChainNamesTheDocumentThisMachineReads() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        state.switchCollection(to: "Default")
        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]),
                 cache: CollectionsLocalCache(synced: ["Team": bound("/Acme/mcp/team.json")], published: [:]))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }

        let team = try XCTUnwrap(model.items.first { $0.name == "Team" })
        XCTAssertEqual(team.source, "/Acme/mcp/team.json")
        // One sentence about one fact: the chain says the same here as on the popover's chip.
        XCTAssertEqual(CollectionsModel.syncedGlyphTooltip(team), "Synced from /Acme/mcp/team.json")
        XCTAssertEqual(CollectionsModel.syncedGlyphTooltip(team),
                       PopoverModel.sourceTooltipFormat("/Acme/mcp/team.json"))

        // A local collection has no source, so no chain and nothing to say about one.
        let local = try XCTUnwrap(model.items.first { $0.name == "Default" })
        XCTAssertNil(local.source)
        XCTAssertNil(CollectionsModel.syncedGlyphTooltip(local))

        // Synced but never found: the sidecar's file name is what the chain can still name, the
        // same fallback the popover's chip takes, so the two never disagree about one collection.
        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]))
        let unlocated = try XCTUnwrap(model.items.first { $0.name == "Team" })
        XCTAssertFalse(unlocated.isLocated)
        XCTAssertEqual(unlocated.source, "team.json")
        XCTAssertEqual(CollectionsModel.syncedGlyphTooltip(unlocated), "Synced from team.json")
        state.switchCollection(to: "Team")
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(CollectionsModel.syncedGlyphTooltip(unlocated), popover.sourceTooltip)
    }
    func testChoosingTheCollectionAlreadyShowingChangesNothing() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Work"))
        state.switchCollection(to: "Default")
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.setChecked("aws-mcp", true)
        var repaints = 0
        let sink = model.objectWillChange.sink { _ in repaints += 1 }
        defer { sink.cancel() }

        // The active collection is what is showing, so naming it again is the same selection —
        // as is naming a collection that does not exist, which falls back to the same one.
        model.selected = "Default"
        model.selected = nil
        model.selected = "No such collection"
        XCTAssertEqual(repaints, 0, "a view writing its selection back must not feed itself")
        XCTAssertEqual(model.checkedNames, ["aws-mcp"], "and the ticks made in it survive")

        // Not remembered either: the window still follows the active collection.
        state.switchCollection(to: "Work")
        XCTAssertEqual(model.selected, "Work")

        // A real change still announces itself.
        model.selected = "Default"
        XCTAssertGreaterThan(repaints, 0)
        XCTAssertEqual(model.selected, "Default")
    }
    /// The Mac's own path: SwiftUI's `List` never writes its selection back, so nothing but the
    /// store change itself can let go of the vanished name. The test below adds a write-back and so
    /// exercises the other trigger; this one would still pass without that trigger and fails only
    /// without the store-change one.
    func testASelectionRenamedAwayIsForgottenWithoutAnyWriteBack() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Spare"))
        state.switchCollection(to: "Default")
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Spare"

        XCTAssertNil(state.renameCollection("Spare", to: "Spare Parts"))
        XCTAssertEqual(model.selected, "Default")
        XCTAssertNil(state.renameCollection("Spare Parts", to: "Spare"))
        XCTAssertEqual(model.selected, "Default", "a returning name must not pull the window to it")
    }

    func testASelectionRenamedAwayDoesNotPullTheWindowBackWhenItReturns() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Spare"))
        state.switchCollection(to: "Default")
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Spare"
        XCTAssertEqual(model.selected, "Spare")

        // Renamed away elsewhere: the window falls back to the active collection.
        XCTAssertNil(state.renameCollection("Spare", to: "Spare Parts"))
        XCTAssertEqual(model.selected, "Default")
        // A view writing its selection back is harmless, and changes nothing either.
        model.selected = "Default"

        // Renamed back: the name resolves again, and the window stays where the user left it.
        XCTAssertNil(state.renameCollection("Spare Parts", to: "Spare"))
        XCTAssertEqual(model.selected, "Default", "a returning name must not pull the window to it")
    }
    func testTheWindowsStripOpensPublishForABlockedPublishAndStillAsksAboutTheFile() throws {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        let folder = h.dir.file("share")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        XCTAssertNil(state.createCollection(named: "Shared"))
        XCTAssertNil(state.startPublishing("Shared", to: folder.path, intent: .none))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Shared"

        let moved = AppState.pathMarkMovedError("ledger")
        state.publishError = CollectionPublishError(collection: "Shared", message: moved, kind: .blockedForReview)
        XCTAssertEqual(model.bannerText, moved)
        XCTAssertEqual(model.bannerButton, CollectionsModel.publishSettingsButton,
                       "a blocked publish is always on a collection that already publishes")
        // False: true would put the Review sheet up. The view shows Publishing Settings for this kind.
        XCTAssertFalse(model.bannerAction())
        XCTAssertEqual(model.choosePublishFolder(h.dir.file("elsewhere").path), moved,
                       "a folder is no answer to this, and the refusal says why")
        XCTAssertEqual(state.collectionsCache.published["Shared"]?.folder, folder.path)

        // Unlike a failed write, a blocked publish never touched the folder, so Stop Publishing can
        // still offer to remove the document there.
        h.dialogs.confirmAnswers = [false]
        model.stopPublishing()
        XCTAssertEqual(h.dialogs.confirms.map(\.message), [CollectionsModel.deletePublishedFileQuestion("shared.json")])
    }

    // MARK: - Selection bar

    /// Where a copy can go: local collections only, never the one the ticked rows already sit in.
    func testCopyDestinationsEnableNeitherTheSourceNorASyncedCollection() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Other"))
        // Created first: the sidecar only annotates a collection already in the master list
        // (CollectionsFile.reconciled(with:)), so seeding "Team" with nothing to annotate would
        // leave it dropped, and missing from copyDestinations for not existing rather than
        // disabled for being synced.
        XCTAssertNil(state.createCollection(named: "Team"))
        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]))
        XCTAssertTrue(state.isSynced("Team"))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)

        model.selected = "Default"
        XCTAssertEqual(model.copyDestinations.filter(\.isEnabled).map(\.name), ["Other"],
                       "not Default, which is the source, and not Team, which is synced")
        model.selected = "Other"
        XCTAssertEqual(model.copyDestinations.filter(\.isEnabled).map(\.name), ["Default"])
    }

    /// The picker's menu: every collection but the source, a synced one listed but disabled so it
    /// can say why.
    func testCopyDestinationsListEveryOtherCollectionAndDisableTheSyncedOnes() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Other"))
        XCTAssertNil(state.createCollection(named: "Team"))
        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]))
        XCTAssertTrue(state.isSynced("Team"))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }

        model.selected = "Default"
        XCTAssertEqual(model.copyDestinations, [
            CollectionsModel.CopyDestination(name: "Other", isEnabled: true),
            CollectionsModel.CopyDestination(name: "Team", isEnabled: false),
        ], "not Default, which is the source; Team is there, but cannot take copies")

        model.selected = "Other"
        XCTAssertEqual(model.copyDestinations.map(\.name), ["Default", "Team"], "the sidebar's order")
        XCTAssertEqual(model.copyDestinations.map(\.isEnabled), [true, false])
    }

    /// Remove needs something ticked.
    func testTheRemovePredicateFollowsTheTicks() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.upsert(name: "alpha", entry: local("/bin/alpha"), renamedFrom: nil, in: "Default"))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)

        model.selected = "Default"
        XCTAssertFalse(model.canRemoveChecked, "nothing ticked yet")
        model.setChecked("alpha", true)
        XCTAssertTrue(model.canRemoveChecked)
    }

    /// The kind guard specifically, not just an empty tick set: `selected` clears the ticks on
    /// every switch (`CollectionsModel.swift` around 204-205), so a leg that ticks a row and only
    /// then switches to the synced collection would find the predicate false regardless of the
    /// `isSynced` term — the empty tick set alone would explain it. Reload does not clear ticks,
    /// so ticking first and letting the *same* collection turn synced underneath is the one path
    /// that isolates the guard.
    func testTheRemovePredicateIsGatedBySyncSpecifically() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        XCTAssertNil(state.upsert(name: "alpha", entry: local("/bin/alpha"), renamedFrom: nil, in: "Team"))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        model.selected = "Team"
        model.setChecked("alpha", true)
        XCTAssertTrue(model.canRemoveChecked, "still local, and something is ticked")

        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]))
        XCTAssertTrue(state.isSynced("Team"))
        XCTAssertEqual(model.checkedNames, ["alpha"], "reload does not clear the ticks")
        XCTAssertFalse(model.canRemoveChecked, "the guard, not an empty tick set, is what changed")
    }

    /// Removing the ticked rows asks first, names the connector when there is one and the count
    /// when there are more, and always says a copy remains in Backups.
    func testRemoveCheckedConfirmsAndCarriesTheBackupsSentence() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        for name in ["alpha", "beta"] {
            XCTAssertNil(state.upsert(name: name, entry: local("/bin/" + name), renamedFrom: nil, in: "Default"))
        }
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        model.selected = "Default"

        // Declined: nothing goes, and the earlier error does not come back.
        presetError(model, h)
        model.setChecked("alpha", true)
        h.dialogs.nextConfirm = false
        model.removeChecked()
        XCTAssertNil(model.lastError, "a declined confirmation clears the last error")
        XCTAssertNotNil(state.store.collections["Default"]?.mcps["alpha"], "declined, so alpha stays")
        XCTAssertNotNil(state.store.collections["Default"]?.mcps["beta"])
        XCTAssertEqual(h.dialogs.confirms.last?.informative, CollectionsModel.removeCheckedInformative)
        XCTAssertEqual(h.dialogs.confirms.last?.destructive, true)
        XCTAssertTrue(h.dialogs.confirms.last?.message.contains("alpha") ?? false, "one connector is named")

        // Accepted, two ticked: the count is stated and the ticks are dropped.
        h.dialogs.nextConfirm = true
        model.setChecked("beta", true)
        model.removeChecked()
        XCTAssertNil(state.store.collections["Default"]?.mcps["alpha"], "both ticked rows went")
        XCTAssertNil(state.store.collections["Default"]?.mcps["beta"])
        XCTAssertTrue(h.dialogs.confirms.last?.message.contains("2") ?? false, "several are counted")
        XCTAssertEqual(model.checkedNames, [], "and the ticks go with them")
        XCTAssertNil(model.lastError, "a removal that lands clears the stale error")
    }

    /// `remove(names:in:)` persists but never applies on its own; the caller applies only when
    /// the collection losing rows is the active one — the same rule `setEnabled` follows.
    func testRemoveCheckedAppliesOnlyWhenTheActiveCollectionLosesRows() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Work"))
        XCTAssertNil(state.upsert(name: "gamma", entry: local("/bin/gamma"), renamedFrom: nil, in: "Work"))
        state.switchCollection(to: "Default")
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        h.dialogs.nextConfirm = true

        // An enabled connector in the active collection that has not been applied yet: an apply
        // from the inactive leg below would write it, so that leg can catch an unconditional one.
        XCTAssertNil(state.upsert(name: "delta", entry: local("/bin/delta"), renamedFrom: nil, in: "Default"))
        XCTAssertEqual(state.store.collections["Default"]?.mcps["delta"]?.enabled, true)
        XCTAssertNil(try h.claudeServers()["delta"], "upserted, not applied")

        // Inactive collection: the write lands, but Claude's config is untouched.
        let before = try h.claudeServers()
        model.selected = "Work"
        model.setChecked("gamma", true)
        model.removeChecked()
        XCTAssertNil(state.store.collections["Work"]?.mcps["gamma"], "removed from the store")
        XCTAssertEqual(try h.claudeServers(), before, "Work was never active, so nothing Claude runs has changed")
        XCTAssertNil(try h.claudeServers()["delta"], "no apply ran, so the pending connector is still unwritten")

        // The active collection: the same call does apply.
        XCTAssertNotNil(before["aws-mcp"], "there before, so its absence below is the apply's doing")
        model.selected = "Default"
        model.setChecked("aws-mcp", true)
        model.removeChecked()
        XCTAssertNil(state.store.collections["Default"]?.mcps["aws-mcp"])
        XCTAssertNil(try h.claudeServers()["aws-mcp"], "the active collection changed, so Claude's config follows")
    }

    /// The copy lands in the target, disabled, and the ticks go with it — the same clearing
    /// `removeChecked` does on success. Every copy arrives disabled, so this never applies.
    func testCopyCheckedCopiesIntoTheTargetClearsTicksAndDoesNotApply() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        // Created first: `createCollection` starts a collection as a copy of whichever one is
        // active when it is made, so making Spare before alpha exists keeps alpha out of that
        // starting snapshot — otherwise the copy below would collide with it and land as
        // "alpha 2" instead, leaving the original untouched and this test green for the wrong
        // reason.
        XCTAssertNil(state.createCollection(named: "Spare"))
        XCTAssertNil(state.upsert(name: "alpha", entry: local("/bin/alpha"), renamedFrom: nil, in: "Default"))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Default"
        model.setChecked("alpha", true)

        presetError(model, h)
        let before = try h.claudeServers()
        XCTAssertTrue(model.copyChecked(into: "Spare"))
        XCTAssertEqual(state.store.collections["Spare"]?.mcps["alpha"]?.enabled, false, "the copy landed, disabled")
        XCTAssertEqual(model.checkedNames, [], "the ticks went with it")
        XCTAssertNil(model.lastError, "a copy that lands clears the stale error")
        XCTAssertEqual(try h.claudeServers(), before, "every copy arrives disabled, so nothing Claude runs has changed")
    }

    /// The two early-outs `copyChecked` documents: a target `copyDestinations` does not enable —
    /// the source itself, or a synced collection — and an empty tick set. Both return false without
    /// reaching `AppState.makeLocalCopy`, so nothing lands anywhere and the ticks are left standing.
    /// The last error is cleared, so the window does not show it again as if this copy had failed.
    func testCopyCheckedRefusesATargetCopyDestinationsDoesNotEnable() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        // Created first, for the same reason as the success test above, and so seeding the
        // sidecar afterwards has something in the master list to annotate
        // (CollectionsFile.reconciled(with:)).
        XCTAssertNil(state.createCollection(named: "Team"))
        XCTAssertNil(state.upsert(name: "alpha", entry: local("/bin/alpha"), renamedFrom: nil, in: "Default"))
        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Default"
        model.setChecked("alpha", true)
        presetError(model, h)

        XCTAssertFalse(model.copyChecked(into: "Default"), "the source is not a target")
        XCTAssertFalse(model.copyChecked(into: "Team"), "a synced collection is not a target either")
        XCTAssertEqual(model.checkedNames, ["alpha"], "both are unreachable early-outs, so the ticks stand")
        XCTAssertNil(model.lastError, "the earlier error is not shown again")
        XCTAssertNil(state.store.collections["Team"]?.mcps["alpha"], "nothing landed in Team")
    }

    func testCopyCheckedRefusesWhenNothingIsTicked() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Spare"))
        let before = state.store.collections["Spare"]
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Default"

        XCTAssertFalse(model.copyChecked(into: "Spare"), "nothing ticked, so there is nothing to copy")
        XCTAssertEqual(state.store.collections["Spare"], before, "and nothing about Spare changed")
    }

    /// Copy to ▸ New Collection: an empty collection holding only the copies, while the window
    /// stays where the rows came from and nothing Claude runs changes.
    func testCopyCheckedIntoNewCollectionMakesAnEmptyCollectionOfTheCopiesAlone() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.upsert(name: "alpha", entry: local("/bin/alpha"), renamedFrom: nil, in: "Default"))
        XCTAssertNil(state.upsert(name: "beta", entry: local("/bin/beta"), renamedFrom: nil, in: "Default"))
        XCTAssertNotNil(state.store.collections["Default"]?.mcps["aws-mcp"])
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Default"
        model.setChecked("alpha", true)
        model.setChecked("beta", true)
        presetError(model, h)

        let before = try h.claudeServers()
        h.dialogs.nextPromptAnswer = "  Fresh  "
        XCTAssertTrue(model.copyCheckedIntoNewCollection())
        XCTAssertEqual(h.dialogs.prompts.last, FakeDialogs.PromptCall(title: AppState.newCollectionTitle, initial: ""))
        let fresh = try XCTUnwrap(state.store.collections["Fresh"], "the store trimmed the name")
        XCTAssertEqual(fresh.mcps.keys.sorted(), ["alpha", "beta"], "the copies alone, not a copy of the active collection")
        XCTAssertNil(fresh.mcps["aws-mcp"])
        XCTAssertEqual(fresh.mcps.values.map(\.enabled), [false, false], "they arrive disabled")
        XCTAssertEqual(model.selected, "Default", "the window stays where the rows came from")
        XCTAssertEqual(state.activeCollection, "Default")
        XCTAssertEqual(model.checkedNames, [], "the ticks went with them")
        XCTAssertNil(model.lastError)
        XCTAssertEqual(try h.claudeServers(), before, "nothing Claude runs has changed")
    }

    func testCopyCheckedIntoNewCollectionCancelledChangesNothing() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.upsert(name: "alpha", entry: local("/bin/alpha"), renamedFrom: nil, in: "Default"))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Default"
        model.setChecked("alpha", true)

        h.dialogs.nextPromptAnswer = nil
        XCTAssertFalse(model.copyCheckedIntoNewCollection())
        XCTAssertEqual(state.collectionNames, ["Default"])
        XCTAssertEqual(model.checkedNames, ["alpha"])
        XCTAssertNil(model.lastError)
    }

    /// A name the store refuses is the store's error, and nothing is made or copied. The ticks
    /// stay for a retry, as they do after any copy that did not land.
    func testCopyCheckedIntoNewCollectionReportsARefusedName() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Work"))
        XCTAssertNil(state.upsert(name: "alpha", entry: local("/bin/alpha"), renamedFrom: nil, in: "Default"))
        state.switchCollection(to: "Default")
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Default"
        model.setChecked("alpha", true)

        h.dialogs.nextPromptAnswer = "Work"
        XCTAssertFalse(model.copyCheckedIntoNewCollection())
        XCTAssertEqual(model.lastError, "A collection named \u{201C}Work\u{201D} already exists.", "the store's own words")
        XCTAssertEqual(state.collectionNames, ["Default", "Work"])
        XCTAssertNil(state.store.collections["Work"]?.mcps["alpha"], "nothing landed in the collection of that name")
        XCTAssertEqual(model.selected, "Default")
        XCTAssertEqual(model.checkedNames, ["alpha"])
    }

    /// The ticked names a destination already holds, sorted, and nothing when none clash.
    func testCheckedNamesClashingNamesTheTickedConnectorsTheDestinationHolds() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Spare"))
        state.switchCollection(to: "Default")
        for name in ["zeta", "alpha", "beta"] {
            XCTAssertNil(state.upsert(name: name, entry: local("/bin/" + name), renamedFrom: nil, in: "Default"))
        }
        for name in ["zeta", "alpha"] {
            XCTAssertNil(state.upsert(name: name, entry: local("/bin/other"), renamedFrom: nil, in: "Spare"))
        }
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Default"
        model.setChecked("zeta", true)
        model.setChecked("beta", true)
        model.setChecked("alpha", true)

        XCTAssertEqual(model.checkedNamesClashing(in: "Spare"), ["alpha", "zeta"], "beta is not in Spare")
        model.setChecked("alpha", false)
        model.setChecked("zeta", false)
        XCTAssertEqual(model.checkedNamesClashing(in: "Spare"), [], "nothing ticked clashes")
    }

    // MARK: - Header pills and menu

    func testPillsMarkActivePublishedAndSubscribedExceptions() throws {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Shared"))
        XCTAssertNil(state.createCollection(named: "Team"))
        XCTAssertNil(state.createCollection(named: "Plain"))
        state.switchCollection(to: "Default")
        let file = CollectionsFile(collections: ["Shared": published(slug: "shared"), "Team": synced(fileName: "team.json")])
        let cache = CollectionsLocalCache(synced: ["Team": bound("/shared/team.json")],
                                          published: ["Shared": .init(folder: "/tmp/share", lastWrittenHash: nil)])
        try seed(h, state, file: file, cache: cache)

        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Default"
        XCTAssertEqual(model.pills, [.active])

        model.selected = "Shared"
        XCTAssertEqual(model.pills, [.published])

        state.switchCollection(to: "Team")
        model.selected = "Team"
        XCTAssertEqual(model.pills, [.active, .subscribed])

        model.selected = "Plain"
        XCTAssertEqual(model.pills, [], "an ordinary local collection, not active, carries no pill")
    }

    func testMenuAndPillTitlesNameTheirStrings() {
        XCTAssertEqual(CollectionsModel.title(for: .active), CollectionsModel.activePill)
        XCTAssertEqual(CollectionsModel.title(for: .published), CollectionsModel.publishedPill)
        XCTAssertEqual(CollectionsModel.title(for: .subscribed), CollectionsModel.subscribedPill)

        XCTAssertEqual(CollectionsModel.title(for: .makeActive), CollectionsModel.makeActiveAction)
        XCTAssertEqual(CollectionsModel.title(for: .rename), CollectionsModel.renameAction)
        XCTAssertEqual(CollectionsModel.title(for: .duplicate), CollectionsModel.duplicateAction)
        XCTAssertEqual(CollectionsModel.title(for: .startPublishing), CollectionsModel.publishButton)
        XCTAssertEqual(CollectionsModel.title(for: .publishingSettings), CollectionsModel.publishSettingsButton)
        XCTAssertEqual(CollectionsModel.title(for: .stopPublishing), CollectionsModel.stopPublishingAction)
        XCTAssertEqual(CollectionsModel.title(for: .showPublishedFile), CollectionsModel.showPublishedFileAction)
        XCTAssertEqual(CollectionsModel.title(for: .exportAll(enabled: true)), CollectionsModel.exportAllAction)
        XCTAssertEqual(CollectionsModel.title(for: .exportAll(enabled: false)), CollectionsModel.exportAllAction)
        XCTAssertEqual(CollectionsModel.title(for: .makeLocalCopy), CollectionsModel.makeLocalCopyButton)
        XCTAssertEqual(CollectionsModel.title(for: .refresh), CollectionsModel.refreshButton)
        XCTAssertEqual(CollectionsModel.title(for: .showSourceFile), CollectionsModel.showSourceFileAction)
        XCTAssertEqual(CollectionsModel.title(for: .stopSyncing), CollectionsModel.stopSyncingAction)
        XCTAssertEqual(CollectionsModel.title(for: .delete(enabled: true)), CollectionsModel.deleteAction)
        XCTAssertEqual(CollectionsModel.title(for: .delete(enabled: false)), CollectionsModel.deleteAction)
        XCTAssertEqual(CollectionsModel.title(for: .separator), "")
    }

    func testCollectionMenuForLocalActiveUnpublished() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        // A second local collection, so the common case is not also the last-collection case.
        XCTAssertNil(state.createCollection(named: "Work"))
        state.switchCollection(to: "Default")
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Default"
        XCTAssertEqual(model.collectionMenu, [
            .rename, .duplicate, .separator,
            .startPublishing, .exportAll(enabled: true), .separator,
            .delete(enabled: true),
        ])
    }

    func testCollectionMenuForLocalPublishedNotActive() throws {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        let folder = h.dir.file("share")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        XCTAssertNil(state.createCollection(named: "Shared"))
        XCTAssertNil(state.startPublishing("Shared", to: folder.path, intent: .none))
        state.switchCollection(to: "Default")   // Shared stays, now not the active collection

        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Shared"
        XCTAssertEqual(model.collectionMenu, [
            .makeActive, .separator,
            .rename, .duplicate, .separator,
            .publishingSettings, .stopPublishing, .showPublishedFile, .exportAll(enabled: true), .separator,
            .delete(enabled: true),
        ])
    }

    func testCollectionMenuForSyncedNotActiveLocated() throws {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        state.switchCollection(to: "Default")
        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Team"

        // Located via a real file, the same flow the banner strip's Locate button drives.
        let document = h.dir.file("team.json")
        try CollectionDocumentSamples.dataTeam.serialized().write(to: document)
        XCTAssertNil(model.locateSource(document.path))
        XCTAssertTrue(state.isLocated("Team"))

        XCTAssertEqual(model.collectionMenu, [
            .makeActive, .separator,
            .rename, .makeLocalCopy, .separator,
            .refresh, .showSourceFile, .stopSyncing, .exportAll(enabled: false), .separator,
            .delete(enabled: true),
        ])
    }

    /// A synced collection whose document has never been found on this machine: nothing to
    /// refresh and nothing to reveal, so both menu rows drop out together.
    func testCollectionMenuOmitsRefreshAndShowSourceFileWhenNotLocated() throws {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        state.switchCollection(to: "Default")
        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Team"

        XCTAssertFalse(state.isLocated("Team"))
        XCTAssertNil(model.sourceFilePath)
        XCTAssertFalse(model.canRefresh)
        XCTAssertFalse(model.collectionMenu.contains(.refresh))
        XCTAssertFalse(model.collectionMenu.contains(.showSourceFile))
        XCTAssertTrue(model.collectionMenu.contains(.stopSyncing), "still offered: nothing is lost by stopping")
    }

    func testCollectionMenuDisablesDeleteForTheLastLocalCollection() throws {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        // Only "Default" exists.
        XCTAssertFalse(model.canDelete)
        XCTAssertTrue(model.collectionMenu.contains(.delete(enabled: false)))
    }

    func testDuplicateCopiesConnectorsDisabledWithoutActivatingAndReportsAClash() throws {
        // Not seeded: createCollection(named:) copies whichever collection is active when it
        // runs, and Default is still active at that point.
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        XCTAssertNil(state.upsert(name: "alpha", entry: local("/bin/alpha"), renamedFrom: nil, in: "Team"))
        XCTAssertNil(state.upsert(name: "beta", entry: local("/bin/beta"), renamedFrom: nil, in: "Team"))
        state.switchCollection(to: "Default")
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }
        model.selected = "Team"

        // Cancelled: nothing changes.
        h.dialogs.nextPromptAnswer = nil
        XCTAssertFalse(model.duplicate())
        XCTAssertEqual(state.collectionNames, ["Default", "Team"])

        let before = try h.claudeServers()
        h.dialogs.nextPromptAnswer = "  Team Copy  "
        XCTAssertTrue(model.duplicate())
        XCTAssertEqual(h.dialogs.prompts.last, FakeDialogs.PromptCall(title: AppState.newCollectionTitle, initial: ""))
        let copy = try XCTUnwrap(state.store.collections["Team Copy"], "the store trimmed the name")
        XCTAssertEqual(copy.mcps.keys.sorted(), ["alpha", "beta"])
        XCTAssertEqual(copy.mcps.values.map(\.enabled), [false, false], "every copy arrives disabled")
        XCTAssertEqual(state.activeCollection, "Default", "duplicate does not activate")
        XCTAssertEqual(try h.claudeServers(), before, "nothing Claude runs has changed")
        XCTAssertEqual(model.selected, "Team", "the selection stays where it was")
        XCTAssertNil(model.lastError)

        // A name the store refuses is the model's error.
        h.dialogs.nextPromptAnswer = "Team Copy"
        XCTAssertFalse(model.duplicate())
        XCTAssertNotNil(model.lastError)
    }

    func testPublishedFilePathAndSourceFilePathNameTheDocumentsThisMachineKnows() throws {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Shared"))
        XCTAssertNil(state.createCollection(named: "Team"))
        state.switchCollection(to: "Default")
        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]))

        let folder = h.dir.file("share")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        XCTAssertNil(state.startPublishing("Shared", to: folder.path, intent: .none))

        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }

        model.selected = "Shared"
        let published = try XCTUnwrap(model.publishedFilePath)
        XCTAssertTrue(published.hasSuffix("shared." + CollectionDocument.fileExtension), published)

        model.selected = "Default"
        XCTAssertNil(model.publishedFilePath, "not published from here")

        model.selected = "Team"
        XCTAssertNil(model.sourceFilePath, "not located yet")
        let document = h.dir.file("team.json")
        try CollectionDocumentSamples.dataTeam.serialized().write(to: document)
        XCTAssertNil(model.locateSource(document.path))
        XCTAssertEqual(model.sourceFilePath, state.sourceLocation(of: "Team"))
        XCTAssertEqual(model.sourceFilePath, document.path)
    }

    // MARK: - Sidebar and connectors header

    func testAddConnectorAffordanceFollowsSyncAndTargetsTheSelectedCollection() throws {
        let (h, state) = AppStateHarness.started(seedClaudeConfig: false)
        defer { h.dispose() }
        XCTAssertNil(state.createCollection(named: "Team"))
        state.switchCollection(to: "Default")
        try seed(h, state, file: CollectionsFile(collections: ["Team": synced(fileName: "team.json")]))
        let model = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { model.dispose() }

        model.selected = "Default"
        XCTAssertTrue(model.canAddConnector)
        XCTAssertEqual(model.addConnectorTooltipText, CollectionsModel.addConnectorTooltip)
        let target = model.newConnectorTarget()
        XCTAssertEqual(target.collection, "Default")
        XCTAssertTrue(target.isNew)

        model.selected = "Team"
        XCTAssertFalse(model.canAddConnector)
        XCTAssertEqual(model.addConnectorTooltipText, CollectionsModel.addConnectorDisabledTooltip)
        XCTAssertEqual(model.newConnectorTarget().collection, "Team")
    }
}

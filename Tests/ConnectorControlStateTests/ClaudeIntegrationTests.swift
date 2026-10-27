import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ClaudeIntegrationTests.cs
@MainActor
final class ClaudeIntegrationTests: XCTestCase {
    private let reserved = ClaudeIntegration.entryName
    private let config = JSONValue.object(["command": .string("node"), "args": .array([.string("x.js")])])

    /// A store written before the name was reserved: the active collection holds a connector
    /// called connector-control, alongside what Claude's file brings in at the first load.
    private func harnessWithALegacyConnector() throws -> (AppStateHarness, AppState) {
        let h = AppStateHarness()
        try MasterStoreIO.save(.single([reserved: MCPEntry(config: config)]), to: h.masterStoreURL)
        return (h, h.create())
    }

    // MARK: Writers refuse the name

    func testAddingAConnectorUnderTheReservedNameIsRefused() {
        let (h, state) = AppStateHarness.started(); defer { h.dispose() }
        XCTAssertEqual(state.upsert(name: reserved, entry: MCPEntry(config: config), renamedFrom: nil),
                       ClaudeIntegration.reservedNameError)
        XCTAssertEqual(state.upsert(name: "  \(reserved) ", entry: MCPEntry(config: config), renamedFrom: nil),
                       ClaudeIntegration.reservedNameError, "trimmed first, as every name is")
        XCTAssertNil(state.store.mcps[reserved])
    }

    func testRenamingAConnectorToTheReservedNameIsRefused() {
        let (h, state) = AppStateHarness.started(); defer { h.dispose() }
        let entry = state.store.mcps["scoutbook"]!
        XCTAssertEqual(state.upsert(name: reserved, entry: entry, renamedFrom: "scoutbook"),
                       ClaudeIntegration.reservedNameError)
        XCTAssertNotNil(state.store.mcps["scoutbook"], "a refused rename leaves the connector where it was")
    }

    func testALegacyConnectorHoldingTheNameCanBeEditedInPlaceOrRenamedAway() throws {
        let (h, state) = try harnessWithALegacyConnector(); defer { h.dispose() }
        let edited = JSONValue.object(["command": .string("node"), "args": .array([.string("y.js")])])
        XCTAssertNil(state.upsert(name: reserved, entry: MCPEntry(config: edited), renamedFrom: reserved))
        XCTAssertEqual(state.store.mcps[reserved]?.config, edited)
        XCTAssertNil(state.upsert(name: "mine", entry: MCPEntry(config: edited), renamedFrom: reserved))
        XCTAssertNil(state.store.mcps[reserved])
        XCTAssertNotNil(state.store.mcps["mine"])
    }

    // MARK: Readers skip it

    func testTheFirstLoadNeverTakesTheEntryIntoTheStore() throws {
        let h = AppStateHarness(); defer { h.dispose() }
        var servers = try h.claudeServers()
        servers[reserved] = ClaudeIntegration.entry(toolPath: "/x/connector-control")
        try ClaudeConfigIO.write(mcpServers: servers, to: h.claudeConfigURL)
        let state = h.create()
        XCTAssertNil(state.store.mcps[reserved])
        XCTAssertNil(try h.storeOnDisk().mcps[reserved])
        XCTAssertEqual(Set(state.store.mcps.keys), ["scoutbook", "aws-mcp", "service-now"])
    }

    func testRestoringABackupThatHoldsTheEntryNeverAdoptsIt() throws {
        let (h, state) = AppStateHarness.started(); defer { h.dispose() }
        let snapshot = h.dir.file("snapshot.json")
        try Data("""
        {"mcpServers": {"connector-control": {"command": "/x/connector-control", "args": ["mcp"]},
                        "scoutbook": {"command": "npx", "args": ["-y", "mcp-remote", "https://scoutbook.example.com/mcp"]}}}
        """.utf8).write(to: snapshot)
        try state.restoreClaudeConfig(from: snapshot)
        XCTAssertNil(state.store.mcps[reserved])
        XCTAssertNil(try h.storeOnDisk().mcps[reserved])
    }

    func testSubscribingSkipsADocumentsConnectorOfThatNameAndSaysWhy() throws {
        let (h, state) = AppStateHarness.started(); defer { h.dispose() }
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors[reserved] = doc.connectors["github"]
        try h.subscribe(state, to: doc)
        XCTAssertNil(state.store.collections["Data team"]?.mcps[reserved])
        XCTAssertEqual(state.sourceBinding(of: "Data team")?.excluded[reserved], ClaudeIntegration.reservedNameError)
    }

    func testImportingSkipsADocumentsConnectorOfThatName() throws {
        let (h, state) = AppStateHarness.started(); defer { h.dispose() }
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors[reserved] = doc.connectors["github"]
        let url = try h.writeDocument(doc, named: "team.json")
        XCTAssertNil(state.importCopies(documentAt: url.path, into: state.activeCollection, choices: [:]))
        XCTAssertNil(state.store.mcps[reserved])
        XCTAssertNotNil(state.store.mcps["github"])
        let model = ImportModel(state: state, path: url.path)
        XCTAssertEqual(model.rows.first { $0.name == reserved }?.badge,
                       ImportModel.skippedBadge(ClaudeIntegration.reservedNameError))
    }

    func testAnUpdateThatAddsAConnectorOfThatNameSkipsIt() throws {
        let (h, state) = AppStateHarness.started(); defer { h.dispose() }
        let url = try h.subscribe(state, to: CollectionDocumentSamples.dataTeam)
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors[reserved] = doc.connectors["github"]
        try h.writeDocument(doc, at: url)
        state.refreshSource(for: "Data team")
        XCTAssertNil(state.pendingUpdates["Data team"], "a connector that never comes across is no update")
        XCTAssertNil(state.applyPendingUpdate(for: "Data team"))
        XCTAssertNil(state.store.collections["Data team"]?.mcps[reserved])
    }

    // MARK: The entry and its strings

    func testTheEntryRunsTheBundledToolAsTheRelay() {
        XCTAssertEqual(ClaudeIntegration.entry(toolPath: "/Applications/Connector Control.app/Contents/MacOS/connector-control"),
                       .object(["command": .string("/Applications/Connector Control.app/Contents/MacOS/connector-control"),
                                "args": .array([.string("mcp")])]))
        XCTAssertEqual(ClaudeIntegration.entryName, "connector-control")
    }

    // MARK: The entry in Claude's config

    private let toolPath = "/Applications/Connector Control.app/Contents/MacOS/connector-control"

    func testTheEntryIsInClaudesConfigOnlyWhileTheSwitchIsOn() throws {
        let h = AppStateHarness(); defer { h.dispose() }
        h.controlToolPath = toolPath
        let state = h.create()
        XCTAssertNil(try h.claudeServers()[reserved])
        XCTAssertNil(state.setClaudeIntegrationEnabled(true))
        XCTAssertEqual(try h.claudeServers()[reserved], ClaudeIntegration.entry(toolPath: toolPath))
        XCTAssertTrue(h.settings.claudeIntegrationEnabled)
        XCTAssertTrue(state.store.collections.values.allSatisfy { $0.mcps[reserved] == nil }, "never stored")
        XCTAssertNil(try h.storeOnDisk().mcps[reserved])
        XCTAssertNil(state.setClaudeIntegrationEnabled(false))
        XCTAssertNil(try h.claudeServers()[reserved])
        XCTAssertEqual(Set(try h.claudeServers().keys), ["scoutbook", "aws-mcp", "service-now"])
    }

    func testTurningItOnWhileClaudeRunsAsksForARestart() throws {
        let h = AppStateHarness(); defer { h.dispose() }
        h.controlToolPath = toolPath
        h.claudeRunningSince(hours: 1)
        let state = h.create()
        XCTAssertFalse(state.needsClaudeRestart)
        state.setClaudeIntegrationEnabled(true)
        XCTAssertTrue(state.needsClaudeRestart)
    }

    func testAReloadWithTheEntryInPlaceIsNotAChange() throws {
        let h = AppStateHarness(); defer { h.dispose() }
        h.controlToolPath = toolPath
        let state = h.create()
        state.setClaudeIntegrationEnabled(true)
        h.notifier.clearSent()
        state.reload()
        XCTAssertFalse(state.isDirty, "the entry read back from Claude's file matches the one the app renders")
        XCTAssertTrue(h.notifier.sent.isEmpty)
        XCTAssertNil(state.store.mcps[reserved])
    }

    func testWithNoBundledToolThereIsNoEntry() throws {
        let (h, state) = AppStateHarness.started(); defer { h.dispose() }
        XCTAssertNil(state.setClaudeIntegrationEnabled(true))
        XCTAssertNil(try h.claudeServers()[reserved])
        XCTAssertFalse(state.isDirty)
    }

    func testThePathFollowsTheApp() throws {
        let h = AppStateHarness(); defer { h.dispose() }
        h.controlToolPath = "/old/place/connector-control"
        h.settings.claudeIntegrationEnabled = true
        let first = h.create()
        XCTAssertEqual(try h.claudeServers()[reserved], ClaudeIntegration.entry(toolPath: "/old/place/connector-control"))
        first.dispose()
        h.controlToolPath = toolPath
        _ = h.create()
        XCTAssertEqual(try h.claudeServers()[reserved], ClaudeIntegration.entry(toolPath: toolPath),
                       "the next launch writes the app's new location")
    }

    func testAClashKeepsTheEntryOutAndTheSwitchRefusesUntilItIsRenamed() throws {
        let h = AppStateHarness(); defer { h.dispose() }
        h.controlToolPath = toolPath
        try MasterStoreIO.save(.single([reserved: MCPEntry(config: config)]), to: h.masterStoreURL)
        let state = h.create()
        XCTAssertTrue(state.claudeIntegrationClash)
        XCTAssertFalse(state.showsClaudeIntegrationOffer)
        XCTAssertEqual(state.setClaudeIntegrationEnabled(true), ClaudeIntegration.clashNote)
        XCTAssertFalse(h.settings.claudeIntegrationEnabled)
        XCTAssertEqual(try h.claudeServers()[reserved], config, "the user's own connector is what Claude runs")
        XCTAssertNil(state.setClaudeIntegrationEnabled(false), "turning it off always goes through")
        XCTAssertNil(state.upsert(name: "mine", entry: MCPEntry(config: config), renamedFrom: reserved))
        state.applyInteractively()
        XCTAssertFalse(state.claudeIntegrationClash)
        XCTAssertNil(state.setClaudeIntegrationEnabled(true))
        XCTAssertEqual(try h.claudeServers()[reserved], ClaudeIntegration.entry(toolPath: toolPath))
        XCTAssertEqual(try h.claudeServers()["mine"], config)
    }

    // MARK: The one-time offer

    func testTheOfferShowsUntilAnsweredAndNotNowDismissesItForGood() {
        let h = AppStateHarness(); defer { h.dispose() }
        h.controlToolPath = toolPath
        let state = h.create()
        XCTAssertTrue(state.showsClaudeIntegrationOffer)
        state.answerClaudeIntegrationOffer(turnOn: false)
        XCTAssertFalse(state.showsClaudeIntegrationOffer)
        XCTAssertTrue(h.settings.claudeIntegrationOfferAnswered)
        XCTAssertFalse(h.settings.claudeIntegrationEnabled)
    }

    func testTurnOnFromTheOfferTurnsItOn() throws {
        let h = AppStateHarness(); defer { h.dispose() }
        h.controlToolPath = toolPath
        let state = h.create()
        state.answerClaudeIntegrationOffer(turnOn: true)
        XCTAssertFalse(state.showsClaudeIntegrationOffer)
        XCTAssertTrue(h.settings.claudeIntegrationEnabled)
        XCTAssertEqual(try h.claudeServers()[reserved], ClaudeIntegration.entry(toolPath: toolPath))
    }

    func testDecidingTheSettingAnywhereAnswersTheOffer() {
        let h = AppStateHarness(); defer { h.dispose() }
        h.controlToolPath = toolPath
        let state = h.create()
        state.setClaudeIntegrationEnabled(false)
        XCTAssertFalse(state.showsClaudeIntegrationOffer, "off, decided in Settings or by the command line")
        XCTAssertTrue(h.settings.claudeIntegrationOfferAnswered)
    }

    func testThereIsNoOfferWithoutABundledTool() {
        let (h, state) = AppStateHarness.started(); defer { h.dispose() }
        XCTAssertFalse(state.showsClaudeIntegrationOffer)
    }
}

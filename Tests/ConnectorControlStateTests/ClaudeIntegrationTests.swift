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
}

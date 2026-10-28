import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ControlToolHostTests.cs
@MainActor
final class ControlToolHostTests: XCTestCase {
    private func connect(_ rig: ControlRig, isCLI: Bool, client: String) -> (McpSession, () -> [String]) {
        var sent: [String] = []
        let host = ControlToolHost(tools: rig.tools, isCLI: isCLI)
        let session = McpSession(host: host, version: host.version, send: { sent.append($0) })
        host.session = session
        session.receive(#"{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25","clientInfo":{"name":"\#(client)"}}}"#)
        return (session, { sent })
    }

    func testToolsListIsTheCatalog() {
        let rig = ControlRig(); defer { rig.dispose() }
        let host = ControlToolHost(tools: rig.tools, isCLI: false)
        XCTAssertEqual(host.listTools(), ControlTools.catalog.map(\.mcpDescriptor))
        XCTAssertEqual(host.version, "1.2.2")
    }

    func testTheRelayIsNamedByTheClientItIntroducedItselfAs() {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.confirmer.scripted = true
        let (session, sent) = connect(rig, isCLI: false, client: "Cursor")
        session.receive(#"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"add_connector","arguments":{"name":"x","config":{"command":"node"}}}}"#)
        rig.h.drain()
        XCTAssertEqual(rig.confirmer.requests.last?.title, ControlTools.addTitle("Cursor", "x"))
        XCTAssertTrue(sent().last?.contains(#""isError":false"#) ?? false)
    }

    func testClaudeDesktopsAgentModeIsClaude() {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.confirmer.scripted = true
        let (session, _) = connect(rig, isCLI: false, client: "local-agent-mode-connector-control")
        session.receive(#"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"add_connector","arguments":{"name":"x","config":{"command":"node"}}}}"#)
        rig.h.drain()
        XCTAssertEqual(rig.confirmer.requests.last?.title, ControlTools.addTitle(ControlCaller.claudeName, "x"))
    }

    func testAClientThatGaveNoNameIsTheRelayAndIsAsked() {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.confirmer.scripted = false
        var sent: [String] = []
        let host = ControlToolHost(tools: rig.tools, isCLI: false)
        let session = McpSession(host: host, version: host.version, send: { sent.append($0) })
        host.session = session
        session.receive(#"{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}"#)
        XCTAssertEqual(host.caller, .relay(clientName: ""))
        session.receive(#"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"add_connector","arguments":{"name":"x","config":{"command":"node"}}}}"#)
        rig.h.drain()
        XCTAssertEqual(rig.confirmer.requests.last?.title, ControlTools.addTitle(ControlCaller.unknownClientName, "x"))
        XCTAssertNil(rig.state.store.mcps["x"])
        XCTAssertTrue(sent.last?.contains(#""isError":true"#) ?? false)
    }

    /// Only the transport's word makes a connection the command-line tool: a relay client that gives
    /// the tool's own name is still the relay, and still asked.
    func testARelayCallingItselfTheCommandLineToolIsStillAsked() {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.confirmer.scripted = false
        let (session, sent) = connect(rig, isCLI: false, client: MasterStore.reservedConnectorName)
        XCTAssertEqual(session.clientName, "connector-control")
        session.receive(#"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"add_connector","arguments":{"name":"x","config":{"command":"node"}}}}"#)
        rig.h.drain()
        XCTAssertEqual(rig.confirmer.requests.map(\.title), [ControlTools.addTitle("connector-control", "x")])
        XCTAssertNil(rig.state.store.mcps["x"])
        XCTAssertTrue(sent().last?.contains(#""isError":true"#) ?? false)
    }

    func testACommandLineConnectionIsNeverAsked() {
        let rig = ControlRig(); defer { rig.dispose() }
        let (session, _) = connect(rig, isCLI: true, client: "connector-control")
        session.receive(#"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"add_connector","arguments":{"name":"x","config":{"command":"node"}}}}"#)
        rig.h.drain()
        XCTAssertTrue(rig.confirmer.requests.isEmpty)
        XCTAssertNotNil(rig.state.store.mcps["x"])
    }

    func testAResultReachesTheWireAsTheMCPResult() throws {
        let rig = ControlRig(); defer { rig.dispose() }
        let (session, sent) = connect(rig, isCLI: false, client: "claude-ai")
        session.receive(#"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_collections"}}"#)
        rig.h.drain()
        let expected = try XCTUnwrap(rig.call("list_collections")).mcpResult
        XCTAssertEqual(sent().last, JSONValue.object(["jsonrpc": .string("2.0"), "id": .int(1), "result": expected]).compactLine())
    }

    /// `tools/call` checks the list on every call: it is built once, from the static catalog, and
    /// every connection's host lists the same descriptors.
    func testEveryHostListsTheOneCachedCatalog() {
        let rig = ControlRig(); defer { rig.dispose() }
        let relay = ControlToolHost(tools: rig.tools, isCLI: false)
        let cli = ControlToolHost(tools: rig.tools, isCLI: true)
        XCTAssertEqual(ControlTools.mcpDescriptors, ControlTools.catalog.map(\.mcpDescriptor))
        XCTAssertEqual(relay.listTools(), ControlTools.mcpDescriptors)
        XCTAssertEqual(cli.listTools(), ControlTools.mcpDescriptors)
        // The Windows mirror also asserts the two are the very same list; JSONValue arrays are values,
        // so Swift has no identity to compare.
    }

    /// A tool or relay newer than the app it reached — an update replaced the app on disk while the
    /// old one still runs — is told to restart the app, not that the tool does not exist.
    func testANewerClientAskingForAToolThisAppLacksIsToldToRestartTheApp() {
        let rig = ControlRig(); defer { rig.dispose() }
        let host = ControlToolHost(tools: rig.tools, isCLI: true)
        let unknown = McpSession.unknownToolMessage("future_tool")
        XCTAssertEqual(host.unknownToolMessage("future_tool"), unknown, "no version was said")
        for older in ["1.2.2", "1.2.1", "1.2.2-preview.1", "development build"] {
            host.clientVersion = older
            XCTAssertEqual(host.unknownToolMessage("future_tool"), unknown, older)
        }
        host.clientVersion = "99.0.0"
        XCTAssertEqual(host.unknownToolMessage("future_tool"), ControlTools.needsNewerAppError("99.0.0"))
        host.clientVersion = "1.3.0-preview.2"
        XCTAssertEqual(host.unknownToolMessage("future_tool"), ControlTools.needsNewerAppError("1.3.0-preview.2"))
    }

    /// The version the session introduces itself with, and the one a hello is compared with, is the
    /// version alone: the Mac shows its build number beside it, Windows does not.
    func testTheToolsVersionIsTheSemanticVersionTheAppShows() {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.updater.versionDisplay = "1.5.0 (10500)"
        XCTAssertEqual(rig.tools.version, "1.5.0")
        rig.updater.versionDisplay = AppVersion.developmentBuild
        XCTAssertEqual(rig.tools.version, AppVersion.developmentBuild, "a development build says so")
    }

    /// The host says when its connection has closed, so a write still waiting for its turn is dropped.
    func testAClosedHostsWaitingWriteIsDropped() {
        let rig = ControlRig(); defer { rig.dispose() }
        rig.confirmer.scripted = nil
        _ = rig.start("delete_connectors", ["names": .array([.string("aws-mcp")])])
        XCTAssertEqual(rig.confirmer.waitingCount, 1, "a dialog holds the line")
        let host = ControlToolHost(tools: rig.tools, isCLI: true)
        var answers: [JSONValue] = []
        host.callTool(name: "switch_collection", arguments: .object(["collection": .string("Default")]), progressToken: nil,
                      progress: { _ in }, completion: { answers.append($0) })
        host.isOpen = false
        rig.confirmer.answer(false)
        rig.h.drain()
        XCTAssertEqual(answers, [], "nobody is left to answer")
    }
}

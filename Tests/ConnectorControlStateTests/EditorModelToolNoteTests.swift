import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/EditorModelTests.cs — the
/// tool-probing/ToolNote slice.
@MainActor
final class EditorModelToolNoteTests: XCTestCase {
    private let url = "https://scoutbook.example.com/mcp"

    func testAdoptingAFormFromJsonEvaluatesTheToolOnceAtTheEnd() {
        // adoptForm assigns command, args and isRemote one after another; with
        // evaluation suppressed until the end, a config whose tool is unchanged
        // costs no probe at all, and a changed one costs exactly one batch.
        let rig = EditorRig()
        defer { rig.dispose() }
        let state = rig.state
        let editor = rig.editor(.new(template: rig.local("node", ["server.js"])))
        defer { editor.dispose() }
        XCTAssertTrue(rig.h.ui.pumpUntil({ state.toolStatuses[.node] != nil }, timeout: 5))
        let batches = rig.h.tools.batches
        editor.requestView(.json)
        editor.jsonText = "{\"command\": \"node\", \"args\": [\"other.js\"]}"
        editor.requestView(.form)
        XCTAssertEqual(editor.view, .form)
        XCTAssertEqual(editor.requiredTool, .node)
        XCTAssertEqual(rig.h.tools.batches, batches, "same tool after adoption: nothing to probe")
        editor.requestView(.json)
        editor.jsonText = "{\"command\": \"uvx\", \"args\": [\"tool\"]}"
        XCTAssertTrue(rig.h.ui.pumpUntil({ rig.h.tools.batches == batches + 1 }, timeout: 5))   // the JSON view evaluates as it parses
        editor.requestView(.form)
        XCTAssertEqual(editor.requiredTool, .uvx)
        XCTAssertEqual(rig.h.tools.batches, batches + 1, "adoption of an already-evaluated config probes nothing more")
    }

    func testNewRemoteConnectorNotesAMissingNpx() throws {
        let rig = EditorRig { $0.tools.statuses[.npx] = .notFound }
        defer { rig.dispose() }
        let editor = rig.editor(.newRemote())
        defer { editor.dispose() }
        XCTAssertEqual(editor.requiredTool, .npx)
        XCTAssertNil(editor.toolNote)   // not probed yet: no note, and nothing blocks
        XCTAssertTrue(rig.h.ui.pumpUntil({ editor.toolNote != nil }, timeout: 5))
        let note = try XCTUnwrap(editor.toolNote)
        XCTAssertEqual(note.text, "npx wasn’t found, so Claude Desktop won’t be able to start this connector.")
        XCTAssertEqual(note.linkTitle, "Install Node.js")
        XCTAssertEqual(note.linkURL.absoluteString, "https://nodejs.org/en/download")
        XCTAssertEqual(note.installCommand, "brew install node")
        editor.name = "example"
        editor.remoteURL = url
        XCTAssertTrue(editor.canSave)   // the note never blocks Save
        XCTAssertTrue(editor.save())
        XCTAssertNil(editor.validationError)
    }

    func testLocalCommandChangesReEvaluateAndReProbeTheTool() throws {
        let rig = EditorRig { $0.tools.statuses[.uvx] = .notFound }
        defer { rig.dispose() }
        let state = rig.state
        let editor = rig.editor(.new(template: rig.local("node", ["server.js"])))
        defer { editor.dispose() }
        XCTAssertEqual(editor.requiredTool, .node)
        XCTAssertTrue(rig.h.ui.pumpUntil({ state.toolStatuses[.node] != nil }, timeout: 5))
        XCTAssertNil(editor.toolNote)   // node is installed on this (fake) machine
        var raised = 0
        let subscription = editor.objectWillChange.sink { _ in raised += 1 }
        defer { subscription.cancel() }
        editor.command = "uvx"
        XCTAssertEqual(editor.requiredTool, .uvx)
        XCTAssertGreaterThan(raised, 0)
        XCTAssertTrue(rig.h.ui.pumpUntil({ editor.toolNote != nil }, timeout: 5))
        XCTAssertTrue(try XCTUnwrap(editor.toolNote).text.hasPrefix("uvx wasn’t found"))
        editor.command = "/usr/local/bin/uvx"   // a path is the user's deliberate choice: no PATH lookup, no note
        XCTAssertNil(editor.requiredTool)
        XCTAssertNil(editor.toolNote)
        editor.command = "python"
        XCTAssertNil(editor.requiredTool)
        XCTAssertEqual(rig.h.tools.probed.count, 2)   // node once, uvx once — the non-tools cost nothing
        editor.command = "uvx"
        // Back to a tool that is cached: probed again anyway — it may have been installed meanwhile.
        XCTAssertTrue(rig.h.ui.pumpUntil({ rig.h.tools.probed.count == 3 }, timeout: 5))
        XCTAssertNotNil(editor.toolNote)
    }

    func testJsonViewEvaluatesTheParsedConfig() {
        let rig = EditorRig { $0.tools.statuses[.uv] = .notFound }
        defer { rig.dispose() }
        let editor = rig.editor(.new(template: rig.local("node", ["x.js"])))
        defer { editor.dispose() }
        editor.requestView(.json)
        XCTAssertEqual(editor.requiredTool, .node)   // the same config, now read from the text
        editor.jsonText = "{\"command\": \"uv\", \"args\": [\"run\", \"server.py\"]}"
        XCTAssertEqual(editor.requiredTool, .uv)
        XCTAssertTrue(rig.h.ui.pumpUntil({ editor.toolNote != nil }, timeout: 5))
        editor.jsonText = "{ not json"
        XCTAssertNil(editor.requiredTool)   // unparseable: nothing to evaluate
        XCTAssertNil(editor.toolNote)
        editor.jsonText = "{\"command\": \"npx\", \"args\": [\"-y\", \"mcp-remote\", \"" + url + "\"]}"
        XCTAssertEqual(editor.requiredTool, .npx)
        editor.requestView(.form)   // a bare bridge invocation: the remote form, still npx
        XCTAssertTrue(editor.isRemote)
        XCTAssertEqual(editor.requiredTool, .npx)
    }

    func testACachedStatusShowsTheNoteAtOnceAndAFoundToolShowsNone() throws {
        let rig = EditorRig { $0.tools.statuses[.npx] = .notFound }
        defer { rig.dispose() }
        let state = rig.state
        state.refreshTools()
        XCTAssertTrue(rig.h.ui.pumpUntil({ state.toolStatuses[.npx] != nil }, timeout: 5))
        let batches = rig.h.tools.batches
        let remote = rig.editor(.existing(name: "scoutbook", entry: try XCTUnwrap(state.store.mcps["scoutbook"])))   // bare npx mcp-remote
        XCTAssertNotNil(remote.toolNote)          // straight from the cache, no wait
        XCTAssertEqual(rig.h.tools.batches, batches)   // and no re-probe on open
        let localEditor = rig.editor(.existing(name: "local", entry: MCPEntry(config: rig.local("node", ["x.js"]))))
        defer { localEditor.dispose() }
        XCTAssertEqual(localEditor.requiredTool, .node)
        XCTAssertNil(localEditor.toolNote)
        XCTAssertEqual(rig.h.tools.batches, batches)
        remote.dispose()
        state.refreshTools([.npx])   // a disposed editor no longer listens
        XCTAssertTrue(rig.h.ui.pumpUntil({ rig.h.tools.batches == batches + 1 }, timeout: 5))
    }

    func testDisposeStopsRelayingAppStateToolStatusChanges() {
        let rig = EditorRig { $0.tools.statuses[.npx] = .notFound }
        defer { rig.dispose() }
        let state = rig.state
        let editor = rig.editor(.newRemote())
        XCTAssertTrue(rig.h.ui.pumpUntil({ editor.toolNote != nil }, timeout: 5))

        editor.dispose()
        var raised = 0
        let subscription = editor.objectWillChange.sink { _ in raised += 1 }
        defer { subscription.cancel() }

        // Publish a change that would clear the note on a live (not disposed) editor.
        rig.h.tools.statuses[.npx] = .found(path: "/opt/homebrew/bin/npx", version: "1.0.0")
        state.refreshTools([.npx])
        XCTAssertTrue(rig.h.ui.pumpUntil({ state.toolStatuses[.npx] == .found(path: "/opt/homebrew/bin/npx", version: "1.0.0") }, timeout: 5))
        XCTAssertEqual(raised, 0)   // the view was never told to re-read: dispose stopped the relay
    }

    /// macOS only: a launcher only the login shell can see gets the advice line.
    func testAShellOnlyToolShowsTheAdviceLine() throws {
        let rig = EditorRig { $0.tools.statuses[.npx] = .foundInShellOnly(path: "/Users/me/.nvm/versions/node/v22/bin/npx", version: "10.9.2") }
        defer { rig.dispose() }
        let editor = rig.editor(.newRemote())
        defer { editor.dispose() }
        XCTAssertTrue(rig.h.ui.pumpUntil({ editor.toolNote != nil }, timeout: 5))
        let note = try XCTUnwrap(editor.toolNote)
        XCTAssertEqual(note.text, "npx is at /Users/me/.nvm/versions/node/v22/bin/npx in your shell, but Claude Desktop launches connectors with its own PATH and may not see it there.")
        XCTAssertEqual(note.advice, ToolNote.shellOnlyAdvice)
        XCTAssertEqual(note.installCommand, "brew install node")
        editor.name = "example"
        editor.remoteURL = url
        XCTAssertTrue(editor.canSave)   // advisory: never blocks Save
    }
}

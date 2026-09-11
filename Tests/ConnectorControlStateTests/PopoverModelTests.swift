import Combine
import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/FlyoutModelTests.cs. Rows are
/// value types identified by name, so "the same row object" becomes "the row
/// with that name".
@MainActor
final class PopoverModelTests: XCTestCase {
    func testHeaderTextsFollowTheStore() {
        let h = AppStateHarness(seedClaudeConfig: false)
        defer { h.dispose() }
        let state = h.create()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(PopoverModel.title, "Connector Control")
        XCTAssertEqual(popover.subtitle, "No connectors configured")
        XCTAssertEqual(popover.profileChipText, "Default ▾")
        XCTAssertTrue(popover.isEmpty)
        XCTAssertEqual(PopoverModel.emptyText, "No connectors configured yet — add one below.")
        XCTAssertNil(state.upsert(name: "z", entry: MCPEntry(config: AppStateHarness.remote("https://z.example/mcp")), renamedFrom: nil))
        XCTAssertEqual(popover.subtitle, "1 of 1 enabled")
        XCTAssertFalse(popover.isEmpty)
    }

    func testRowsAreSortedOrdinallyWithEditTooltips() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        XCTAssertNil(state.upsert(name: "Zebra", entry: MCPEntry(config: AppStateHarness.remote("https://zebra.example/mcp")), renamedFrom: nil))
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(popover.rows.map(\.name), ["Zebra", "aws-mcp", "scoutbook", "service-now"])   // uppercase first: ordinal
        XCTAssertEqual(popover.rows[1].editTooltip, "Edit “aws-mcp”")
        XCTAssertTrue(popover.rows.allSatisfy(\.enabled))
    }

    func testTogglingARowPersistsAndAppliesThroughAppState() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        popover.setEnabled("aws-mcp", false)
        XCTAssertEqual(try h.storeOnDisk().mcps["aws-mcp"]?.enabled, false)
        XCTAssertNil(try h.claudeServers()["aws-mcp"])
        XCTAssertEqual(popover.subtitle, "2 of 3 enabled")
        XCTAssertEqual(popover.rows.first { $0.name == "aws-mcp" }?.enabled, false)   // the row is still there, now off
    }

    func testRowsFollowExternalStateChanges() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        // The rows read through to AppState; the republish in init is what makes
        // the popover repaint, and it is the one line a passthrough cannot prove.
        var repaints = 0
        let sink = popover.objectWillChange.sink { _ in repaints += 1 }
        defer { sink.cancel() }
        state.setEnabled("aws-mcp", false)
        XCTAssertEqual(popover.rows.first { $0.name == "aws-mcp" }?.enabled, false)
        XCTAssertGreaterThan(repaints, 0, "an AppState change is republished to the view")
        state.remove(name: "scoutbook")
        XCTAssertEqual(popover.rows.map(\.name), ["aws-mcp", "service-now"])
        XCTAssertNil(state.upsert(name: "alpha", entry: MCPEntry(config: AppStateHarness.remote("https://alpha.example/mcp")), renamedFrom: nil))
        XCTAssertEqual(popover.rows.map(\.name), ["alpha", "aws-mcp", "service-now"])
        // dispose() cuts the republish: the rows still read through, nothing repaints.
        popover.dispose()
        let before = repaints
        state.setEnabled("alpha", false)
        XCTAssertEqual(popover.rows.first { $0.name == "alpha" }?.enabled, false)
        XCTAssertEqual(repaints, before)
    }

    func testProfileMenuItemsAndTitles() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(popover.profileItems, [ProfileMenuItem(name: "Default", isActive: true)])
        XCTAssertEqual(PopoverModel.newProfileTitle, "New Profile…")
        XCTAssertEqual(popover.renameProfileTitle, "Rename “Default”…")
        XCTAssertEqual(popover.deleteProfileTitle, "Delete “Default”…")
        XCTAssertFalse(popover.canDeleteProfile)

        h.dialogs.nextPromptAnswer = "Work"
        popover.newProfile()
        XCTAssertEqual(popover.profileItems, [ProfileMenuItem(name: "Default", isActive: false), ProfileMenuItem(name: "Work", isActive: true)])
        XCTAssertEqual(popover.profileChipText, "Work ▾")
        XCTAssertTrue(popover.canDeleteProfile)
        popover.switchProfile("Default")
        XCTAssertEqual(popover.profileChipText, "Default ▾")
    }

    func testFooterPrefersRetryOverRestart() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.claude.isRunning = true
        h.claude.launchDate = h.now.addingTimeInterval(-3600)
        let state = h.create()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(popover.footer, FooterKind.hidden)
        XCTAssertFalse(popover.showFooter)

        state.setEnabled("aws-mcp", false)
        XCTAssertEqual(popover.footer, .restartRequired)
        XCTAssertEqual(popover.footerTitle, "Restart Required")
        XCTAssertEqual(popover.footerGlyph, "arrow.clockwise")
        XCTAssertTrue(popover.showFooter)

        try Data("{oops".utf8).write(to: h.claudeConfigURL)
        state.setEnabled("scoutbook", false)   // apply fails
        XCTAssertEqual(popover.footer, .retryApply)
        XCTAssertEqual(popover.footerTitle, "Apply Failed — Retry")
        XCTAssertEqual(popover.footerGlyph, "exclamationmark.arrow.circlepath")
        XCTAssertNotNil(popover.errorMessage)

        try Data(Fixtures.realisticClaudeConfig.utf8).write(to: h.claudeConfigURL)
        popover.footerAction()   // retry
        XCTAssertEqual(popover.footer, .restartRequired)
        XCTAssertEqual(try h.claudeServers().keys.sorted(), ["service-now"])
    }

    func testFooterActionRestartsWhenRestartIsRequired() {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.settings.confirmBeforeRestart = false
        h.claude.isRunning = true
        h.claude.launchDate = h.now.addingTimeInterval(-3600)
        let state = h.create()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        state.setEnabled("aws-mcp", false)
        popover.footerAction()
        XCTAssertEqual(h.claude.restartCalls, 1)
    }

    func testOpenedRunsARoutineReload() throws {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        try h.writeClaudeServers([("scoutbook", try XCTUnwrap(state.store.mcps["scoutbook"]).config)])
        popover.opened()
        XCTAssertEqual(try h.claudeServers().keys.sorted(), ["aws-mcp", "scoutbook", "service-now"])
        XCTAssertEqual(h.notifier.sent[0].body, AppState.claudeConfigRegeneratedBody)
    }

    func testEntryForReturnsTheLiveEntryOrNull() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(popover.entryFor("scoutbook"), state.store.mcps["scoutbook"])
        XCTAssertNil(popover.entryFor("gone"))
    }

    func testRowsCarryTheToolWarningAndFollowLaterProbeResults() {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.tools.statuses[.npx] = .notFound
        let state = h.create()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertTrue(popover.rows.allSatisfy { $0.toolWarning == nil })   // nothing probed yet: no glyph

        state.refreshTools([.npx])
        XCTAssertTrue(h.ui.pumpUntil({ state.toolStatuses[.npx] != nil }, timeout: 5))
        // All three seeded connectors run `npx -y mcp-remote`.
        XCTAssertTrue(popover.rows.allSatisfy { $0.toolWarning == "Needs npx, which wasn’t found. Edit to see how to install it." })

        // A connector whose command is a full path needs no PATH lookup, so it never warns.
        XCTAssertNil(state.upsert(name: "pathed", entry: MCPEntry(config: .object(["command": .string("/usr/local/bin/node")])), renamedFrom: nil))
        let pathed = popover.rows.first { $0.name == "pathed" }
        XCTAssertNil(pathed?.toolWarning)
        XCTAssertNotNil(popover.rows.first { $0.name == "aws-mcp" }?.toolWarning)   // the others are unchanged

        // Installing npx: the next probe publishes found and every glyph clears.
        h.tools.statuses[.npx] = .found(path: "/opt/homebrew/bin/npx", version: "10.9.2")
        state.refreshTools([.npx])
        XCTAssertTrue(h.ui.pumpUntil({ popover.rows.allSatisfy { $0.toolWarning == nil } }, timeout: 5))
        XCTAssertTrue(popover.rows.allSatisfy(\.enabled))   // the glyph never touched the switch
    }

    /// macOS only: a launcher only the login shell can see.
    func testRowsCarryTheShellOnlyWarning() {
        let h = AppStateHarness()
        defer { h.dispose() }
        h.tools.statuses[.npx] = .foundInShellOnly(path: "/Users/me/.nvm/versions/node/v22/bin/npx", version: "10.9.2")
        let state = h.create()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        popover.opened()
        XCTAssertTrue(h.ui.pumpUntil({ popover.rows.allSatisfy { $0.toolWarning != nil } }, timeout: 5))
        XCTAssertTrue(popover.rows.allSatisfy { $0.toolWarning == "Needs npx, which Claude Desktop may not see. Edit to see how to fix it." })
        XCTAssertTrue(popover.rows.allSatisfy(\.enabled))
    }

    func testOpenedProbesOnlyTheToolsTheRowsNeedAndOnlyOnce() {
        let h = AppStateHarness()
        defer { h.dispose() }
        let state = h.create()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        XCTAssertEqual(h.tools.batches, 0)   // building the model probes nothing

        popover.opened()
        XCTAssertTrue(h.ui.pumpUntil({ state.toolStatuses[.npx] != nil }, timeout: 5))
        // Three npx connectors: one tool, one batch — not one probe per row.
        XCTAssertEqual(h.tools.probed, [.npx])
        XCTAssertEqual(h.tools.batches, 1)

        popover.opened()   // everything the rows need is cached now
        XCTAssertEqual(h.tools.batches, 1)
        XCTAssertEqual(h.tools.probed, [.npx])
    }

    func testOpenedProbesNothingWhenNoRowNeedsATool() {
        let h = AppStateHarness(seedClaudeConfig: false)
        defer { h.dispose() }
        let state = h.create()
        let popover = PopoverModel(state: state)
        defer { popover.dispose() }
        popover.opened()   // an empty catalog
        XCTAssertEqual(h.tools.batches, 0)

        XCTAssertNil(state.upsert(name: "pathed", entry: MCPEntry(config: .object(["command": .string("/usr/local/bin/node")])), renamedFrom: nil))
        XCTAssertNil(state.upsert(name: "stranger", entry: MCPEntry(config: .object(["command": .string("python")])), renamedFrom: nil))
        popover.opened()   // a full path and an unknown launcher both need no PATH lookup
        XCTAssertEqual(h.tools.batches, 0)
        XCTAssertTrue(h.tools.probed.isEmpty)
        XCTAssertTrue(popover.rows.allSatisfy { $0.toolWarning == nil })
    }

    func testStringsMatchTheCatalog() {
        XCTAssertEqual(PopoverModel.title, "Connector Control")
        XCTAssertEqual(PopoverModel.addTooltip, "Add Connector")
        XCTAssertEqual(PopoverModel.settingsTooltip, "Settings")
        XCTAssertEqual(PopoverModel.quitTooltip, "Quit Connector Control")
        XCTAssertEqual(PopoverModel.emptyText, "No connectors configured yet — add one below.")
        XCTAssertEqual(PopoverModel.retryTitle, "Apply Failed — Retry")
        XCTAssertEqual(PopoverModel.restartTitle, "Restart Required")
        XCTAssertEqual(PopoverModel.newProfileTitle, "New Profile…")
        XCTAssertEqual(PopoverModel.profileChipText("Work"), "Work ▾")
        XCTAssertEqual(PopoverModel.renameProfileTitle("Work"), "Rename “Work”…")
        XCTAssertEqual(PopoverModel.deleteProfileTitle("Work"), "Delete “Work”…")
        XCTAssertEqual(PopoverModel.retryGlyph, "exclamationmark.arrow.circlepath")
        XCTAssertEqual(PopoverModel.restartGlyph, "arrow.clockwise")
        XCTAssertEqual(PopoverModel.toolWarningGlyph, "exclamationmark.triangle.fill")
        XCTAssertEqual(ConnectorRow(name: "x", enabled: true, toolWarning: nil).editTooltip, "Edit “x”")
    }
}

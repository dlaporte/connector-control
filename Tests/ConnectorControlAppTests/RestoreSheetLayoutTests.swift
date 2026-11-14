import AppKit
import SwiftUI
import XCTest
import ConnectorControlCore
import ConnectorControlState
import ConnectorControlTestSupport
@testable import ConnectorControl

/// Mac only: the Windows counterpart is windows/tests/ConnectorControl.App.Tests/SettingsWindowTests.cs's
/// RestoreDialogKeepsOneSizeOnBothTabs.
///
/// The Restore sheet keeps one size whatever it shows, measured on the real AppKit views over an AppState
/// whose every folder is a temp folder: no Application Support folder, Claude config or defaults domain of
/// this Mac's is read or written.
@MainActor
final class RestoreSheetLayoutTests: XCTestCase {
    // MARK: The sandbox

    @MainActor private final class Settings: AppSettings {
        var masterStoreDir: String?
        var claudeAppPath: String?
        var backupKeepCount = 20
        var notifyExternalChanges = true
        var confirmBeforeRestart = true
        var confirmBeforeQuit = true
        var claudeIntegrationEnabled = false
        var confirmClaudeChanges = true
        var claudeIntegrationOfferAnswered = false
        var lastApplyDate: Date?
        var sweepVersion = 0
    }

    @MainActor private final class Claude: ClaudeProcess {
        var isRunning: Bool { false }
        var launchDate: Date? { nil }
        func restart(completion: @escaping @Sendable (String?) -> Void) { completion(nil) }
    }

    @MainActor private final class Notices: Notifier {
        var onRestartAction: MainActorAction?
        func notify(title: String, body: String, category: String?) {}
    }

    @MainActor private final class Answers: Dialogs {
        func confirm(message: String, informative: String?, primary: String, cancel: String, destructive: Bool,
                     cancelIsDefault: Bool) -> Bool { false }
        func promptForName(title: String, initial: String) -> String? { nil }
        func inform(message: String, informative: String?) {}
        func confirmChange(_ dialog: ConfirmationDialog) -> Bool { false }
    }

    private struct NoTools: ToolProbing {
        func probe(_ tools: [Tool]) -> [Tool: ToolStatus] { [:] }
    }

    /// An AppState over a temp folder, with five collections besides Default; in its backups folder a readable
    /// master-list backup holding all six, one that cannot be read, and five backups of Claude's config beside
    /// the first-run original.
    @MainActor private final class Sandbox {
        static let good = "mcps.2026-01-02T00-00-00-000Z.json"
        static let unreadable = "mcps.2026-01-01T00-00-00-000Z.json"

        let dir = TempDir(prefix: "restore-sheet")
        let state: AppState

        init() throws {
            let appSupport = dir.file("Library/Application Support")
            let claudeConfig = appSupport.appendingPathComponent("Claude/claude_desktop_config.json")
            try FileManager.default.createDirectory(at: claudeConfig.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data(Fixtures.realisticClaudeConfig.utf8).write(to: claudeConfig)
            // Every post is dropped: nothing runs later or in the background, so nothing changes under the layout.
            let host = AppHost(marshal: { _ in }, delay: { _, _ in }, now: { Date(timeIntervalSince1970: 1_788_000_000) },
                               background: { _ in })
            state = AppState(settings: Settings(), claude: Claude(), notifier: Notices(), dialogs: Answers(),
                             paths: PathContext(environment: [:], appSupport: appSupport), host: host, toolProbe: NoTools())
            let backups = state.service.paths.backupsDirURL
            XCTAssertTrue(backups.path.hasPrefix(dir.url.path), "the backups are the sandbox's")
            for name in ["Alpha", "Beta", "Gamma", "Delta", "Epsilon"] {
                XCTAssertNil(state.addEmptyCollection(named: name))
            }
            try FileManager.default.createDirectory(at: backups, withIntermediateDirectories: true)
            try MasterStoreIO.save(state.store, to: backups.appendingPathComponent(Sandbox.good))
            try Data("not a master list".utf8).write(to: backups.appendingPathComponent(Sandbox.unreadable))
            for day in 1...5 {
                try Data(Fixtures.realisticClaudeConfig.utf8)
                    .write(to: backups.appendingPathComponent("claude_desktop_config.2026-01-0\(day)T00-00-00-000Z.json"))
            }
        }

        func dispose() {
            state.dispose()
            dir.dispose()
        }
    }

    // MARK: Laying the sheet out

    private struct Laid {
        let window: NSWindow
        let hosting: NSHostingView<RestoreSheetView>
        /// The sheet's lists, top to bottom.
        let tables: [NSTableView]
        /// The size the sheet asks for: the size a sheet showing it takes.
        let size: CGSize
    }

    /// Lets SwiftUI apply what the models published and AppKit lay out what that changed.
    private func settle(_ hosting: NSView) {
        for _ in 0..<5 {
            RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.02))
            hosting.layoutSubtreeIfNeeded()
        }
    }

    /// The sheet over `model` and `collections` as they are now, open on `tab`, in a window of its own size.
    private func lay(_ model: RestoreModel, _ collections: CollectionRestoreModel, on tab: RestoreSheetView.Tab) -> Laid {
        let hosting = NSHostingView(rootView: RestoreSheetView(model: model, collections: collections, tab: tab))
        let window = NSWindow(contentRect: NSRect(origin: .zero, size: hosting.fittingSize), styleMask: [.titled],
                              backing: .buffered, defer: true)
        window.appearance = NSAppearance(named: .aqua)
        window.contentView = hosting
        settle(hosting)
        window.setContentSize(hosting.fittingSize)
        settle(hosting)
        let tables = views(of: NSTableView.self, in: hosting)
            .sorted { $0.convert($0.bounds, to: nil).maxY > $1.convert($1.bounds, to: nil).maxY }
        return Laid(window: window, hosting: hosting, tables: tables, size: hosting.fittingSize)
    }

    private func views<T: NSView>(of type: T.Type, in view: NSView) -> [T] {
        view.subviews.flatMap { ($0 as? T).map { [$0] } ?? views(of: type, in: $0) }
    }

    /// Saves a picture of `laid`, with the window around it, as `restore-sheet-<name>.png` in the folder
    /// CONNECTOR_CONTROL_RENDERS names, for a person to look at; with none named (CI, and every run by default)
    /// it saves nothing.
    private func render(_ laid: Laid, _ name: String) throws {
        guard let folder = ProcessInfo.processInfo.environment["CONNECTOR_CONTROL_RENDERS"], !folder.isEmpty else { return }
        let view: NSView = laid.hosting.superview ?? laid.hosting
        let rep = try XCTUnwrap(view.bitmapImageRepForCachingDisplay(in: view.bounds))
        view.cacheDisplay(in: view.bounds, to: rep)
        let png = try XCTUnwrap(rep.representation(using: .png, properties: [:]))
        try png.write(to: URL(fileURLWithPath: folder).appendingPathComponent("restore-sheet-\(name).png"))
    }

    /// The first five rows of `table` lie inside what its list shows.
    private func assertShowsFiveRows(_ table: NSTableView, _ list: String, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertGreaterThanOrEqual(table.numberOfRows, 5, "\(list) has five rows to show", file: file, line: line)
        guard table.numberOfRows >= 5 else { return }
        let visible = table.visibleRect, fifth = table.rect(ofRow: 4)
        XCTAssertTrue(visible.minY <= table.rect(ofRow: 0).minY + 0.5 && fifth.maxY <= visible.maxY + 0.5,
                      "\(list) shows five rows: row 5 ends at \(fifth.maxY), the list at \(visible.maxY)", file: file, line: line)
    }

    private func models(_ sandbox: Sandbox) -> (RestoreModel, CollectionRestoreModel) {
        let model = RestoreModel(state: sandbox.state), collections = CollectionRestoreModel(state: sandbox.state)
        model.load()
        collections.load()
        return (model, collections)
    }

    private func choose(_ name: String, in collections: CollectionRestoreModel) {
        collections.selection = collections.backups.first { $0.url.lastPathComponent == name }?.url
        XCTAssertNotNil(collections.selection, name)
    }

    // MARK: Tests

    /// Collections with nothing chosen, with a backup chosen and its collections listed, and with a backup that
    /// cannot be read; Claude's config with and without its failure line: one size throughout, so switching tabs,
    /// choosing a backup or a failure appearing never moves the switch or the buttons. Each list on Collections
    /// shows at least five rows, and the two share the space evenly.
    func testTheSheetKeepsOneSizeOnBothTabs() throws {
        let sandbox = try Sandbox()
        defer { sandbox.dispose() }
        var sizes: [String: CGSize] = [:]

        let (model, collections) = models(sandbox)
        XCTAssertGreaterThanOrEqual(collections.backups.count, 5)
        let empty = lay(model, collections, on: .collections)
        sizes["Collections, nothing chosen"] = empty.size
        try render(empty, "collections")
        XCTAssertEqual(empty.tables.count, 2, "Collections shows two lists")
        XCTAssertEqual(empty.tables.last?.numberOfRows, 0, "no collections until a backup is chosen")

        choose(Sandbox.good, in: collections)
        XCTAssertGreaterThanOrEqual(collections.rows.count, 5)
        let chosen = lay(model, collections, on: .collections)
        sizes["Collections, a backup chosen"] = chosen.size
        try render(chosen, "collections-chosen")
        XCTAssertEqual(chosen.tables.count, 2)
        if chosen.tables.count == 2 {
            XCTAssertEqual(chosen.tables[1].numberOfRows, collections.rows.count, "the chosen backup's collections are listed")
            assertShowsFiveRows(chosen.tables[0], "Backups")
            assertShowsFiveRows(chosen.tables[1], "Collections in this backup")
            XCTAssertEqual(chosen.tables[0].visibleRect.height, chosen.tables[1].visibleRect.height, accuracy: 1,
                           "the two lists share the space evenly")
        }

        choose(Sandbox.unreadable, in: collections)
        XCTAssertNotNil(collections.restoreError, "a backup that cannot be read says so")
        let failed = lay(model, collections, on: .collections)
        sizes["Collections, a failure line"] = failed.size
        try render(failed, "collections-failure")

        let config = lay(model, collections, on: .claudeConfig)
        sizes["Claude's config"] = config.size
        try render(config, "claude-config")
        XCTAssertEqual(config.tables.count, 1, "Claude's config shows one list")
        if let table = config.tables.first { assertShowsFiveRows(table, "Claude's config") }

        let doomed = try XCTUnwrap(model.backups.first)
        try FileManager.default.removeItem(at: doomed)
        model.selection = doomed
        model.requestRestore()
        XCTAssertNotNil(model.restoreError, "a backup gone since the list was read says so")
        let configFailed = lay(model, collections, on: .claudeConfig)
        sizes["Claude's config, a failure line"] = configFailed.size
        try render(configFailed, "claude-config-failure")

        let first = try XCTUnwrap(sizes["Collections, nothing chosen"])
        XCTAssertEqual(first.width, 460, "the sheet stays 460 wide")
        for (shown, size) in sizes.sorted(by: { $0.key < $1.key }) {
            XCTAssertEqual(size, first, "\(shown): \(size) against \(first)")
        }
    }
}

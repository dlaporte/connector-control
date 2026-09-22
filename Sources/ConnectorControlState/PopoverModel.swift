import Combine
import ConnectorControlCore

/// PopoverView, minus pixels: header, error banner, rows, footer,
/// and every action it wires. Everything is computed from AppState; the model
/// only forwards AppState's objectWillChange so the view re-reads.
@MainActor
public final class PopoverModel: ObservableObject {
    public static let title = "Connector Control"
    public static let addTooltip = "Add Connector"
    public static let settingsTooltip = "Settings"
    public static let quitTooltip = "Quit Connector Control"
    public static let emptyText = "No connectors configured yet — add one below."
    public static let retryTitle = "Apply Failed — Retry"
    public static let restartTitle = "Restart Required"
    public static let addDisabledTooltip = "Additions go in a local collection."
    public static let reviewAndApplyButton = "Review & Apply…"
    public static let chooseFolderButton = "Choose Folder…"
    public static let importTitle = "Import…"
    public static let manageTitle = "Manage Collections…"
    /// SF Symbols: the retry footer, the restart footer, and the row caution glyph.
    public static let retryGlyph = "exclamationmark.arrow.circlepath"
    public static let restartGlyph = "arrow.clockwise"
    public static let toolWarningGlyph = "exclamationmark.triangle.fill"

    public static func collectionChipText(_ active: String) -> String { "\(active) ▾" }

    public static func exportTitle(_ active: String) -> String { "Export “\(active)”…" }

    public static func locateButton(_ fileName: String) -> String { "Locate \(fileName)…" }

    /// The chain glyph's tooltip. Named with the `Format` suffix because the Windows mirror
    /// cannot carry a static and an instance member under one name.
    public static func sourceTooltipFormat(_ source: String) -> String { "Synced from \(source)" }

    private let state: AppState
    private var subscription: AnyCancellable?

    public init(state: AppState) {
        self.state = state
        subscription = state.objectWillChange.sink { [weak self] _ in self?.objectWillChange.send() }
    }

    // MARK: header

    public var subtitle: String { state.headerSubtitle }

    public var collectionChipText: String { PopoverModel.collectionChipText(state.activeCollection) }

    /// The menu's Export item, which names the collection it would write.
    public var exportTitle: String { PopoverModel.exportTitle(state.activeCollection) }

    /// The chain beside the chip, and the lock on every row below it.
    public var activeCollectionIsSynced: Bool { state.activeCollectionIsSynced }

    /// The amber dot beside the chip: the collection in front of the user has news at its
    /// source. Another collection's pending update is the menu's dot, not the chip's.
    public var activeHasPendingUpdate: Bool { state.pendingUpdates[state.activeCollection] != nil }

    /// The chain's tooltip: where the active collection's document sits on this machine, or the
    /// name the sidecar recorded while the file has still to be found. nil for a local
    /// collection, which has no source, and for a synced one the sidecar never named.
    public var sourceTooltip: String? {
        let active = state.activeCollection
        guard state.isSynced(active) else { return nil }
        guard let source = state.sourceBinding(of: active)?.path
            ?? state.collectionsFile.collections[active]?.fileName else { return nil }
        return PopoverModel.sourceTooltipFormat(source)
    }

    public var collectionItems: [CollectionMenuItem] {
        let active = state.activeCollection
        return state.collectionNames.map {
            CollectionMenuItem(name: $0, isActive: $0 == active, isSynced: state.isSynced($0),
                               hasPendingUpdate: state.pendingUpdates[$0] != nil)
        }
    }

    /// Nothing can be added to a synced collection: its content is the source file's.
    public var canAddConnector: Bool { !state.activeCollectionIsSynced }

    /// The Add button's tooltip, which says why it is disabled when it is. Picked here rather
    /// than in the view, because XAML binds one tooltip and cannot choose between two statics.
    public var addTooltipText: String {
        canAddConnector ? PopoverModel.addTooltip : PopoverModel.addDisabledTooltip
    }

    // MARK: banner

    public var errorMessage: String? { state.lastError }

    /// The Windows mirror also carries `HasCollectionBanner`: XAML cannot bind a row's visibility
    /// to "this optional is not nil", where SwiftUI binds the optional itself.
    public var collectionBanner: CollectionBanner? { state.collectionBanner }

    public var collectionBannerText: String? {
        switch state.collectionBanner {
        case .updateAvailable(let collection, let summary):
            return AppState.collectionUpdateBanner(collection, summary)
        case .locate(let collection, _):
            return AppState.collectionLocateBanner(collection)
        case .publishFailed(let collection, let message):
            // The folder is the binding's, not the banner's: publishing is what sets the error,
            // so the collection that failed always has one.
            return AppState.collectionPublishFailedBanner(
                collection, state.collectionsCache.published[collection]?.folder ?? "", message)
        case nil:
            return nil
        }
    }

    public var collectionBannerButton: String? {
        switch state.collectionBanner {
        case .updateAvailable: return PopoverModel.reviewAndApplyButton
        case .locate(_, let fileName): return PopoverModel.locateButton(fileName)
        case .publishFailed: return PopoverModel.chooseFolderButton
        case nil: return nil
        }
    }

    // MARK: rows

    public var rows: [ConnectorRow] {
        // Locked together or not at all: the rows are the active collection's, so one being the
        // author's makes all of them so.
        let locked = state.activeCollectionIsSynced
        return state.store.mcps.sorted { $0.key < $1.key }.map { name, entry in
            ConnectorRow(name: name, enabled: entry.enabled, toolWarning: warning(for: entry),
                         isLocked: locked)
        }
    }

    public var isEmpty: Bool { state.store.mcps.isEmpty }

    /// One row's caution-glyph tooltip, by the rule the editor and Settings
    /// also use: the entry's required tool, then that tool's cached status.
    private func warning(for entry: MCPEntry) -> String? {
        guard let tool = ToolRequirement.requiredTool(for: entry.config) else { return nil }
        return ToolNote.rowWarning(tool: tool, status: state.toolStatuses[tool])
    }

    // MARK: footer

    public var footer: FooterKind {
        if state.applyRetryNeeded { return .retryApply }
        if state.needsClaudeRestart { return .restartRequired }
        return .hidden
    }

    public var showFooter: Bool { footer != .hidden }

    public var footerTitle: String { footer == .retryApply ? PopoverModel.retryTitle : PopoverModel.restartTitle }

    public var footerGlyph: String { footer == .retryApply ? PopoverModel.retryGlyph : PopoverModel.restartGlyph }

    // MARK: actions

    /// The popover's onAppear: a routine reload on every open, then the rows' launchers.
    public func opened() {
        state.reload()
        probeRowTools()
    }

    /// The tools the listed connectors need that are not cached yet. Nothing
    /// required, or everything cached, spawns no process; AppState coalesces
    /// a tool already in flight.
    private func probeRowTools() {
        let needed = ToolRequirement.requiredTools(for: state.store.mcps.values.map(\.config))
            .filter { state.toolStatuses[$0] == nil }
        guard !needed.isEmpty else { return }
        state.refreshTools(needed)
    }

    /// The row switch: persists and applies immediately.
    public func setEnabled(_ name: String, _ on: Bool) { state.setEnabled(name, on) }

    public func switchCollection(_ name: String) { state.switchCollection(to: name) }

    /// The banner's button, for the one banner that needs nothing from the user first. False
    /// says the view has still to run its file picker and call `locateSource` or
    /// `choosePublishFolder` with what it gets, which only a view can do.
    @discardableResult
    public func collectionBannerAction() -> Bool {
        guard case .updateAvailable = state.collectionBanner else { return false }
        requestReview()
        return true
    }

    /// The Locate banner's file, for the collection that banner names. nil on success, else the
    /// message; also nil when the banner has moved on since the picker opened, because there is
    /// then no collection asking to be pointed anywhere.
    public func locateSource(_ path: String) -> String? {
        guard case .locate(let collection, _) = state.collectionBanner else { return nil }
        return state.locateSource(for: collection, path: path)
    }

    /// The failed-publish banner's folder, for the collection that banner names. Publishing
    /// again into a new folder is what re-points it, so the recorded intent travels unchanged —
    /// the sheet is where what the document says gets edited. nil as `locateSource` returns nil.
    public func choosePublishFolder(_ path: String) -> String? {
        guard case .publishFailed(let collection, _) = state.collectionBanner else { return nil }
        let intent = state.collectionsFile.collections[collection]?.publish?.intent ?? .none
        return state.startPublishing(collection, to: path, intent: intent)
    }

    /// The menu's Import…: the picker and the sheet belong to the Collections window, so opening
    /// it is all the popover does and this is what it finds waiting.
    public func requestImport() { state.collectionsWindowRequest = .importFile }

    /// The menu's Export “<active>”…, which the Collections window shows for the collection that
    /// is active now rather than whichever one it last had selected.
    public func requestExport() { state.collectionsWindowRequest = .exportActive }

    /// The banner's Review & Apply…, for the collection the banner names — which is not always
    /// the active one, so the name travels with the request.
    public func requestReview() {
        guard case .updateAvailable(let collection, _) = state.collectionBanner else { return }
        state.collectionsWindowRequest = .review(collection: collection)
    }

    public func quit() { state.quitApp() }

    /// The single footer button: retry the apply, or restart Claude.
    public func footerAction() {
        if state.applyRetryNeeded {
            state.apply()
        } else if state.needsClaudeRestart {
            state.restartClaude()
        }
    }

    /// The pencil button opens the editor only if the entry still exists in the store.
    public func entryFor(_ name: String) -> MCPEntry? { state.store.mcps[name] }

    /// Stops listening to AppState. The app does not call this: the
    /// subscription holds `self` weakly and dies with the `@StateObject`, and
    /// the popover's object survives a close and re-open, so calling it from
    /// onDisappear would freeze the second opening. Tests call it to prove the
    /// republish is what repaints the view.
    public func dispose() {
        subscription?.cancel()
        subscription = nil
    }
}

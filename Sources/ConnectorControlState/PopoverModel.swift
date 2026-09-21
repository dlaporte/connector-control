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
    public static let newCollectionTitle = "New Collection…"
    /// SF Symbols: the retry footer, the restart footer, and the row caution glyph.
    public static let retryGlyph = "exclamationmark.arrow.circlepath"
    public static let restartGlyph = "arrow.clockwise"
    public static let toolWarningGlyph = "exclamationmark.triangle.fill"

    public static func collectionChipText(_ active: String) -> String { "\(active) ▾" }

    public static func renameCollectionTitle(_ active: String) -> String { "Rename “\(active)”…" }

    public static func deleteCollectionTitle(_ active: String) -> String { "Delete “\(active)”…" }

    private let state: AppState
    private var subscription: AnyCancellable?

    public init(state: AppState) {
        self.state = state
        subscription = state.objectWillChange.sink { [weak self] _ in self?.objectWillChange.send() }
    }

    // MARK: header

    public var subtitle: String { state.headerSubtitle }

    public var collectionChipText: String { PopoverModel.collectionChipText(state.activeCollection) }

    public var collectionItems: [CollectionMenuItem] {
        let active = state.activeCollection
        return state.collectionNames.map { CollectionMenuItem(name: $0, isActive: $0 == active) }
    }

    public var renameCollectionTitle: String { PopoverModel.renameCollectionTitle(state.activeCollection) }

    public var deleteCollectionTitle: String { PopoverModel.deleteCollectionTitle(state.activeCollection) }

    public var canDeleteCollection: Bool { state.collectionNames.count >= 2 }

    // MARK: banner

    public var errorMessage: String? { state.lastError }

    // MARK: rows

    public var rows: [ConnectorRow] {
        state.store.mcps.sorted { $0.key < $1.key }.map { name, entry in
            ConnectorRow(name: name, enabled: entry.enabled, toolWarning: warning(for: entry))
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

    public func newCollection() { state.newCollection() }

    public func renameCollection() { state.renameCollection() }

    public func deleteCollection() { state.deleteCollection() }

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

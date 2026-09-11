import Foundation
import Combine
import ConnectorControlCore

/// Catalog §2 PopoverView, minus pixels: header, error banner, rows, footer,
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
    public static let newProfileTitle = "New Profile…"
    /// SF Symbols: the retry footer, the restart footer, and the row caution glyph.
    public static let retryGlyph = "exclamationmark.arrow.circlepath"
    public static let restartGlyph = "arrow.clockwise"
    public static let toolWarningGlyph = "exclamationmark.triangle.fill"

    public static func profileChipText(_ active: String) -> String { "\(active) ▾" }

    public static func renameProfileTitle(_ active: String) -> String { "Rename “\(active)”…" }

    public static func deleteProfileTitle(_ active: String) -> String { "Delete “\(active)”…" }

    private let state: AppState
    private var subscription: AnyCancellable?

    public init(state: AppState) {
        self.state = state
        subscription = state.objectWillChange.sink { [weak self] _ in self?.objectWillChange.send() }
    }

    // MARK: header (catalog §2.2)

    public var subtitle: String { state.headerSubtitle }

    public var profileChipText: String { PopoverModel.profileChipText(state.activeProfile) }

    public var profileItems: [ProfileMenuItem] {
        let active = state.activeProfile
        return state.profileNames.map { ProfileMenuItem(name: $0, isActive: $0 == active) }
    }

    public var renameProfileTitle: String { PopoverModel.renameProfileTitle(state.activeProfile) }

    public var deleteProfileTitle: String { PopoverModel.deleteProfileTitle(state.activeProfile) }

    public var canDeleteProfile: Bool { state.profileNames.count >= 2 }

    // MARK: banner (catalog §2.3)

    public var errorMessage: String? { state.lastError }

    public var hasError: Bool { state.lastError != nil }

    // MARK: rows (catalog §2.4)

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

    // MARK: footer (catalog §2.5)

    public var footer: FooterKind {
        if state.applyRetryNeeded { return .retryApply }
        if state.needsClaudeRestart { return .restartRequired }
        return FooterKind.none
    }

    public var showFooter: Bool { footer != FooterKind.none }

    public var footerTitle: String { footer == .retryApply ? PopoverModel.retryTitle : PopoverModel.restartTitle }

    public var footerGlyph: String { footer == .retryApply ? PopoverModel.retryGlyph : PopoverModel.restartGlyph }

    // MARK: actions (catalog §2.6)

    /// The popover's onAppear: a routine reload on every open, then the rows' launchers.
    public func opened() {
        state.reload()
        probeRowTools()
    }

    /// The tools the listed connectors need that are not cached yet (addendum
    /// 2026-09-06-row-glyph §3). Nothing required, or everything cached,
    /// spawns no process; AppState coalesces a tool already in flight.
    private func probeRowTools() {
        let needed = ToolRequirement.requiredTools(for: state.store.mcps.values.map(\.config))
            .filter { state.toolStatuses[$0] == nil }
        guard !needed.isEmpty else { return }
        state.refreshTools(needed)
    }

    /// The row switch: persists and applies immediately.
    public func setEnabled(_ name: String, _ on: Bool) { state.setEnabled(name, on) }

    public func switchProfile(_ name: String) { state.switchProfile(to: name) }

    public func newProfile() { state.newProfile() }

    public func renameProfile() { state.renameProfile() }

    public func deleteProfile() { state.deleteProfile() }

    public func quit() { state.quitApp() }

    /// The single footer button: retry the apply, or restart Claude.
    public func footerAction() {
        if state.applyRetryNeeded {
            state.apply()
        } else if state.needsClaudeRestart {
            state.restartClaude()
        }
    }

    /// The pencil button opens the editor only if the entry still exists in the store (catalog §2.4).
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

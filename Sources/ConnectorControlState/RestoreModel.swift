import Foundation
import Combine
import ConnectorControlCore

/// Catalog §5 RestoreSheetView state. The confirmation is a sheet:
/// requestRestore opens it (and clears the previous attempt's error),
/// confirmRestore is its Restore button, cancelRestore its implicit Cancel.
@MainActor
public final class RestoreModel: ObservableObject {
    public static let headline = "Restore Claude config from a backup"
    public static let caption = "The current file is backed up first, then replaced by the selected backup."
    public static let cancelTitle = "Cancel"
    public static let restoreTitle = "Restore…"
    public static let restoreButton = "Restore"
    public static let series = "claude_desktop_config"

    public static func confirmMessage(fileName: String) -> String { "Replace Claude's config with \(fileName)?" }

    private let state: AppState

    /// Full URLs, newest first; the permanent .original snapshot last (catalog §5).
    @Published public private(set) var backups: [URL] = []
    @Published public var selection: URL?
    @Published public private(set) var restoreError: String?
    /// True while the confirmation sheet is up.
    @Published public private(set) var confirming = false

    public init(state: AppState) {
        self.state = state
    }

    public var backupNames: [String] { backups.map(\.lastPathComponent) }

    public var canRestore: Bool { selection != nil }

    public var hasRestoreError: Bool { restoreError != nil }

    public var confirmMessage: String {
        RestoreModel.confirmMessage(fileName: selection?.lastPathComponent ?? "")
    }

    public func load() {
        var found = (try? state.service.backups.backups(series: RestoreModel.series)) ?? []
        let original = state.service.backups.backupsDir
            .appendingPathComponent("\(RestoreModel.series).original.json")
        if FileManager.default.fileExists(atPath: original.path) {
            found.append(original)
        }
        backups = found
    }

    /// The Restore… button. A fresh attempt starts with a clean sheet: the
    /// previous attempt's error must not outlive a new selection or a cancelled
    /// confirmation. Then the confirmation sheet opens.
    public func requestRestore() {
        restoreError = nil
        guard selection != nil else { return }
        confirming = true
    }

    public func cancelRestore() { confirming = false }

    /// The sheet's Restore button: restore through AppState (which syncs the
    /// baseline). True when restored and the sheet should close.
    public func confirmRestore() -> Bool {
        confirming = false
        guard let backup = selection else { return false }
        do {
            try state.restoreClaudeConfig(from: backup)
            return true
        } catch {
            restoreError = error.localizedDescription   // raw message, not friendly(): catalog §5
            state.lastError = error.localizedDescription
            return false
        }
    }
}

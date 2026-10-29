import Foundation
import Combine
import ConnectorControlCore

/// RestoreSheetView state. The confirmation is a sheet:
/// requestRestore opens it (and clears the previous attempt's error),
/// confirmRestore is its Restore button, cancelRestore its implicit Cancel.
@MainActor
public final class RestoreModel: ObservableObject {
    public static let headline = "Restore Claude config from a backup"
    public static let caption = "The current file is backed up first, then replaced by the selected backup."
    public static let cancelTitle = "Cancel"
    /// The button that opens this sheet from Settings and the sheet's own Restore, on both
    /// platforms; `restoreButton` is the confirmation's, a separate question with its own title.
    public static let restoreTitle = "Restore"
    public static let restoreButton = "Restore"
    public static let series = "claude_desktop_config"

    public static func confirmMessage(fileName: String) -> String { "Replace Claude's config with \(fileName)?" }

    private let state: AppState

    /// Full URLs, newest first; the permanent .original snapshot last.
    @Published public private(set) var backups: [URL] = []
    @Published public var selection: URL?
    @Published public private(set) var restoreError: String?
    /// True while the confirmation sheet is up.
    @Published public private(set) var confirming = false
    /// What the confirmation was asked about: the selected backup's record and content as Restore
    /// read them, so what is restored is the backup as it was when the user was asked.
    private var asked: (backup: URL, recorded: String?, data: Data)?

    public init(state: AppState) {
        self.state = state
    }

    public var canRestore: Bool { selection != nil }

    public var confirmMessage: String {
        RestoreModel.confirmMessage(fileName: selection?.lastPathComponent ?? "")
    }

    public func load() {
        do {
            backups = try state.service.backups.restorableBackups(series: RestoreModel.series)
        } catch {
            restoreError = error.localizedDescription
        }
    }

    /// The Restore button. A fresh attempt starts with a clean sheet: the
    /// previous attempt's error must not outlive a new selection or a cancelled
    /// confirmation. A restore AppState would refuse says why at once, rather
    /// than after a confirmation it could never honour. Then the confirmation
    /// sheet opens.
    public func requestRestore() {
        restoreError = nil
        asked = nil
        guard let backup = selection else { return }
        let recorded = BackupCollections.collection(of: backup, in: state.service.paths.backupsDirURL)
        if let refusal = state.restoreRefusal(forRecorded: recorded) {
            restoreError = refusal.localizedDescription
            return
        }
        // A backup that cannot be read says so at once, as a refusal does: there is nothing to ask about.
        // Platform idiom: every error lands here; the Windows mirror catches the I/O and access errors
        // a read throws.
        do {
            asked = (backup, recorded, try BoundedRead.data(at: backup, limit: ConfigService.maxBackupBytes))
        } catch {
            restoreError = error.localizedDescription
            return
        }
        confirming = true
    }

    /// Leaves `asked` alone: SwiftUI may close the confirmation, which lands here, before it runs the
    /// Restore button's action, and the next `requestRestore()` replaces it anyway.
    public func cancelRestore() { confirming = false }

    /// The sheet's Restore button: restore through AppState (which syncs the
    /// baseline). True when restored and the sheet should close.
    public func confirmRestore() -> Bool {
        confirming = false
        guard let asked else { return false }
        self.asked = nil
        do {
            try state.restoreClaudeConfig(from: asked.backup, data: asked.data, recorded: asked.recorded)
            return true
        } catch let refusal as RestoreError {
            // A refusal is the sheet's alone, as the one before the confirmation is: nothing was
            // restored, so there is nothing for the banner to say. It arrives here only when the
            // collections changed while the confirmation was up.
            restoreError = refusal.localizedDescription
            return false
        } catch {
            // A restore that failed is the banner's too.
            restoreError = error.localizedDescription   // raw message, not friendly()
            state.lastError = error.localizedDescription
            return false
        }
    }
}

import Foundation
import Combine
import ConnectorControlCore

/// The Restore sheet's Collections tab: the master list's backups, the collections the chosen one holds
/// and how each stands against the store now, and the restore of one of them as it was then. Every
/// backup is read once, when the tab loads, and a restore restores the bytes read then, so what the tab
/// shows is what it restores. The lines a restore's confirmation lists, one per connector it changes
/// (`CollectionRestorePlan.lines(naming:)`), are `restore_collection`'s too; each takes the name as its
/// caller shows it.
///
/// The confirmation is a sheet: `requestRestore` opens it (and clears the previous attempt's error),
/// `confirmRestore` is its Restore button, `cancelRestore` its implicit Cancel.
///
/// Mirror: windows/src/ConnectorControl.Core/State/CollectionRestoreModel.cs
@MainActor
public final class CollectionRestoreModel: ObservableObject {
    /// The master list's backups, as `ConfigService.saveStore` names them: the ones this tab and
    /// `list_backups` list, and `restore_collection` takes.
    nonisolated public static let series = "mcps"

    nonisolated public static let caption = "Choose a backup, then a collection. The collection goes back to exactly how it was; its current connectors are backed up first and kept for 30 days."
    nonisolated public static let goneState = "deleted since"
    nonisolated public static let sameState = "same as now"
    nonisolated public static let differsState = "differs from now"
    nonisolated public static let subscribedState = "subscribed now"
    /// Joins a collection row's connector count and its state, and the row's name to that detail where
    /// the row is spoken.
    nonisolated public static let detailSeparator = " · "
    nonisolated public static let activeInformative = "Claude is running this collection, so Claude’s config is updated too."

    nonisolated public static func confirmMessage(_ collection: String, _ time: String) -> String { "Restore “\(collection)” as it was at \(time)?" }

    nonisolated public static func createdInformative(_ collection: String) -> String { "“\(collection)” no longer exists, so it is created again." }

    nonisolated public static func comesBackLine(_ name: String, _ on: Bool) -> String { on ? "+ \(name) (on)" : "+ \(name) (off)" }

    nonisolated public static func changesLine(_ name: String, _ on: Bool) -> String { on ? "~ \(name) (on)" : "~ \(name) (off)" }

    /// "−" is U+2212, as `ControlMasking.removedPrefix` marks a removed line.
    nonisolated public static func goesLine(_ name: String) -> String { "− \(name)" }

    nonisolated public static func unchangedLine(_ count: Int) -> String { "= \(count) unchanged" }

    /// Under the confirmation's message: the plan's lines, one per line, then, after a blank line, that a
    /// gone collection is created again or that the active one updates Claude's config too. A gone
    /// collection that held no connectors has no lines, and says only that.
    nonisolated public static func informative(for plan: CollectionRestorePlan) -> String {
        let after = plan.created ? createdInformative(plan.collection) : plan.active ? activeInformative : nil
        return [plan.lines().joined(separator: "\n"), after ?? ""].filter { !$0.isEmpty }.joined(separator: "\n\n")
    }

    /// How a collection in a backup stands against the store now.
    public enum RowState: Equatable {
        /// The store has no such collection: a restore creates it again.
        case gone
        /// It is already as the backup holds it.
        case same
        /// A restore would change it.
        case differs
        /// It is subscribed now: its connectors are its author's.
        case subscribed

        public var text: String {
            switch self {
            case .gone: return CollectionRestoreModel.goneState
            case .same: return CollectionRestoreModel.sameState
            case .differs: return CollectionRestoreModel.differsState
            case .subscribed: return CollectionRestoreModel.subscribedState
            }
        }

        /// Only a collection a restore would change can be restored.
        public var restorable: Bool { self == .gone || self == .differs }
    }

    /// A master-list backup as the tab read it when it loaded.
    public struct Backup: Identifiable {
        public let url: URL
        /// Its time, as `BackupManager.labels(for:)` names it among the backups listed.
        public let label: String
        /// Its bytes as read then, nil when they could not be: what a restore from it restores.
        let data: Data?
        /// Those bytes decoded, nil when they could not be read or decoded.
        let store: MasterStore?

        public var id: URL { url }
    }

    /// One of the chosen backup's collections: its name as the backup holds it, how it stands now, and
    /// the detail shown beside the name, its connector count and state.
    public struct Row: Identifiable, Equatable {
        public let name: String
        public let state: RowState
        public let detail: String

        public var id: String { name }
        /// What a screen reader says for the row: the name, then the detail.
        public var spokenName: String { name + CollectionRestoreModel.detailSeparator + detail }
    }

    private let state: AppState

    /// Newest first; the series has no first-run original.
    @Published public private(set) var backups: [Backup] = []
    /// The chosen backup, none when the tab opens. Choosing another clears the chosen collection and
    /// lists the new one's collections; one that could not be read says so, and lists none.
    @Published public var selection: URL? {
        didSet {
            guard selection != oldValue else { return }
            collection = nil
            restoreError = selectedBackup.flatMap { $0.store == nil ? AppState.collectionBackupUnreadableError($0.url.lastPathComponent) : nil }
            rows = selectedBackup.map(rows(of:)) ?? []
        }
    }
    /// The chosen backup's collections, by name ordinally.
    @Published public private(set) var rows: [Row] = []
    /// The chosen collection, by its row's name.
    /// Choosing one re-reads the chosen backup's rows, so a row that changed since the backup was chosen
    /// shows how it stands now.
    @Published public var collection: String? {
        didSet {
            guard collection != nil, collection != oldValue, let backup = selectedBackup else { return }
            rows = rows(of: backup)
        }
    }
    @Published public private(set) var restoreError: String?
    /// True while the confirmation sheet is up.
    @Published public private(set) var confirming = false
    /// What the confirmation was asked about: the backup as the tab read it, the collection chosen, and
    /// the plan those bytes gave.
    private var asked: (backup: Backup, collection: String, plan: CollectionRestorePlan)?

    public init(state: AppState) {
        self.state = state
    }

    private var selectedBackup: Backup? { backups.first { $0.url == selection } }

    /// Only a collection that is deleted since or differs from now can be restored.
    public var canRestore: Bool {
        guard let collection else { return false }
        return rows.first { $0.name == collection }?.state.restorable ?? false
    }

    public var confirmMessage: String {
        guard let asked else { return "" }
        return CollectionRestoreModel.confirmMessage(asked.plan.collection, asked.backup.label)
    }

    public var confirmInformative: String? { asked.map { CollectionRestoreModel.informative(for: $0.plan) } }

    /// Lists the master list's backups and reads each one once, within the bound every backup read
    /// keeps, as `list_backups` reads them.
    public func load() {
        do {
            let urls = try state.service.backups.backups(series: CollectionRestoreModel.series)
            let labels = BackupManager.labels(for: urls)
            backups = urls.map { url in
                let data = try? BoundedRead.data(at: url, limit: ConfigService.maxBackupBytes)
                return Backup(url: url, label: labels[url.lastPathComponent] ?? url.lastPathComponent,
                              data: data, store: data.flatMap { try? MasterStoreIO.decode($0) })
            }
        } catch {
            restoreError = error.localizedDescription
        }
    }

    /// The Restore button. A restore AppState would refuse says why at once, rather than after a
    /// confirmation it could never honour; otherwise the confirmation sheet opens. A fresh attempt
    /// starts with a clean sheet: the previous attempt's error must not outlive it.
    public func requestRestore() {
        guard let backup = selectedBackup, let collection else { return }
        restoreError = nil
        asked = nil
        do {
            asked = (backup, collection, try state.collectionRestorePlan(collection, id: backup.url.lastPathComponent, data: backup.data))
        } catch {
            refused(error, in: backup)
            return
        }
        confirming = true
    }

    /// Leaves `asked` alone: SwiftUI may close the confirmation, which lands here, before it runs the
    /// Restore button's action, and the next `requestRestore()` replaces it anyway.
    public func cancelRestore() { confirming = false }

    /// Opens the confirmation again on the plan `confirmRestore()` asked about anew, when there is one.
    /// Platform idiom: SwiftUI may close the confirmation after its button's action has run, and so after
    /// that action asked again, so the sheet calls this on the next turn; the Windows mirror's confirmation
    /// is modal, and its Restore asks again in a loop.
    public func askAgain() {
        guard asked != nil else { return }
        confirming = true
    }

    /// The confirmation's Restore button: restore the bytes the tab read through AppState. True when
    /// restored and the sheet should close. The plan is worked out again first: one that differs from
    /// what the confirmation showed, the collection having changed while it was up, is asked about
    /// again with its own lines, never restored unseen.
    public func confirmRestore() -> Bool {
        confirming = false
        guard let asked else { return false }
        self.asked = nil
        let plan: CollectionRestorePlan
        do {
            plan = try state.collectionRestorePlan(asked.collection, id: asked.backup.url.lastPathComponent, data: asked.backup.data)
        } catch {
            // The plan throws only its refusals, which are the sheet's alone, as the restore's are below.
            refused(error, in: asked.backup)
            return false
        }
        // Equal plans list the same lines under the same message.
        guard plan == asked.plan else {
            self.asked = (asked.backup, asked.collection, plan)
            confirming = true
            return false
        }
        do {
            // Never nil here: bytes that could not be read give no plan, and none would restore anyway.
            try state.restoreCollection(asked.collection, from: asked.backup.url, data: asked.backup.data ?? Data())
            return true
        } catch let refusal as CollectionRestoreError {
            // A refusal is the sheet's alone, as the one before the confirmation is: nothing was
            // restored, so there is nothing for the banner to say. The plan worked out just above
            // would have refused first.
            refused(refusal, in: asked.backup)
            return false
        } catch {
            // A restore that failed is the banner's too.
            restoreError = error.localizedDescription   // raw message, not friendly()
            state.lastError = error.localizedDescription
            return false
        }
    }

    /// A refusal shows on the sheet's failure line, and the rows say how the collections stand now.
    private func refused(_ refusal: Error, in backup: Backup) {
        restoreError = refusal.localizedDescription
        rows = rows(of: backup)
    }

    /// The backup's collections by name ordinally, each with how it stands now: the plan the restore
    /// would follow, or the refusal that would stop it.
    private func rows(of backup: Backup) -> [Row] {
        guard let store = backup.store else { return [] }
        return store.collections.sorted { $0.key.ordinallyPrecedes($1.key) }.map { name, held in
            let rowState: RowState
            do {
                rowState = try state.collectionRestorePlan(name, id: backup.url.lastPathComponent, data: backup.data).created ? .gone : .differs
            } catch CollectionRestoreError.subscribed {
                rowState = .subscribed
            } catch CollectionRestoreError.unchanged {
                rowState = .same
            } catch {
                // The backup's own collection in bytes it decoded: no other refusal reaches here. Asked
                // to restore it, the refusal says why.
                rowState = .differs
            }
            let count = ControlTools.connectorCountText(held.mcps.count, held.mcps.values.filter(\.enabled).count)
            return Row(name: name, state: rowState, detail: count + CollectionRestoreModel.detailSeparator + rowState.text)
        }
    }
}

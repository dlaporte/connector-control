import Combine
import Foundation
import ConnectorControlCore

/// The Collections window, minus pixels: the collections as items in the left pane, the selected
/// collection's connectors as rows in the right one, the detail line above them, and a toolbar
/// whose every button follows the selection. Everything is derived from AppState; the model owns
/// only what the window itself knows — which collection is showing and which rows are ticked.
///
/// Mirror: windows/src/ConnectorControl.Core/State/CollectionsModel.cs
@MainActor
public final class CollectionsModel: ObservableObject {
    public static let windowTitle = "Collections"
    public static let importButton = "Import…"
    public static let subscribeButton = "Subscribe…"
    public static let publishButton = "Publish…"
    public static let refreshButton = "Refresh"
    public static let makeLocalCopyButton = "Make Local Copy…"
    public static let newButton = "New…"
    public static let renameAction = "Rename…"
    public static let deleteAction = "Delete…"
    public static let stopPublishingAction = "Stop Publishing"
    public static let stopSyncingAction = "Stop Syncing (keeps a local copy)"
    public static let activeSuffix = " · active"
    public static let unlocatedDetail = "synced · file not located on this machine"
    /// The source's status in the detail line. The cache records no timestamp, so this says what
    /// is true of the file, not how long ago it last changed.
    public static let updateAvailableStatus = "update available"
    public static let upToDateStatus = "up to date"
    /// The two answers to the published-document question. Keep is the default: a file the team
    /// reads is not something to remove by pressing Return.
    public static let removeFileButton = "Remove"
    public static let keepFileButton = "Keep"
    public static let remoteType = "remote"

    public static func exportButton(_ count: Int) -> String { "Export \(count)…" }

    public static func localType(_ command: String) -> String { "local · \(command)" }

    public static func localDetail(_ count: Int) -> String { "local · \(count) connectors" }

    /// `source` is the document's path on this machine, never the sidecar's origin, which is a UUID.
    public static func syncedDetail(_ source: String, _ status: String) -> String { "synced from \(source) · read-only · \(status)" }

    /// "this Mac" is the platform-forced half of this sentence; the Windows mirror says "this PC".
    public static func publishedDetail(_ folder: String) -> String { "publishes to \(folder) from this Mac" }

    public static func deletePublishedFileQuestion(_ fileName: String) -> String { "Also remove \(fileName) from the folder?" }

    /// One collection in the left pane. A published collection carries no mark of its own there —
    /// `isPublished` is what the detail line says, not a sidebar glyph.
    public struct Item: Identifiable, Equatable, Sendable {
        public let id: String
        public let name: String
        public let kind: CollectionKind
        public let isActive: Bool
        public let isPublished: Bool
        public let hasPendingUpdate: Bool
        public let isLocated: Bool

        public init(name: String, kind: CollectionKind, isActive: Bool, isPublished: Bool,
                    hasPendingUpdate: Bool, isLocated: Bool) {
            self.id = name
            self.name = name
            self.kind = kind
            self.isActive = isActive
            self.isPublished = isPublished
            self.hasPendingUpdate = hasPendingUpdate
            self.isLocated = isLocated
        }
    }

    /// One connector of the selected collection. `checked` is the window's own state — an export
    /// tick, not anything the store holds — so it is the one field the model fills in itself.
    public struct Row: Identifiable, Equatable, Sendable {
        public let id: String
        public let name: String
        public let enabled: Bool
        public let caution: String?
        public let isLocked: Bool
        public var checked: Bool
        public let typeText: String

        public init(name: String, enabled: Bool, caution: String?, isLocked: Bool, checked: Bool, typeText: String) {
            self.id = name
            self.name = name
            self.enabled = enabled
            self.caution = caution
            self.isLocked = isLocked
            self.checked = checked
            self.typeText = typeText
        }
    }

    /// What the last action AppState refused reported, cleared by the next one that succeeds.
    @Published public private(set) var lastError: String?

    private let state: AppState
    private let dialogs: Dialogs
    private var subscriptions: Set<AnyCancellable> = []
    /// What the view last picked, which may name a collection that no longer exists; `selected`
    /// resolves it. Stored rather than published because a copy of AppState's answers kept here
    /// would be one change behind: `@Published` fires before the value it announces is in place.
    private var selection: String?
    private var checkedNames_: Set<String> = []
    /// Which collection the ticks above belong to. The window shows one collection at a time, and
    /// a tick must not survive into another one that happens to hold a connector of that name.
    private var checkedCollection: String?

    public init(state: AppState, dialogs: Dialogs) {
        self.state = state
        self.dialogs = dialogs
        // Everything this window reads: the store behind the items and rows, the sidecar and the
        // cache behind their marks, and the two derived maps behind the detail line's status.
        relay(state.$store)
        relay(state.$collectionsFile)
        relay(state.$collectionsCache)
        relay(state.$pendingUpdates)
        relay(state.$sourceErrors)
    }

    private func relay<P: Publisher>(_ publisher: P) where P.Failure == Never {
        publisher.dropFirst()
            .sink { [weak self] _ in self?.objectWillChange.send() }
            .store(in: &subscriptions)
    }

    // MARK: - Selection

    /// The collection the right pane is showing. It defaults to the active one and falls back to
    /// it whenever the chosen name stops being a collection — deleted here, or renamed from
    /// anywhere else.
    public var selected: String? {
        get { selectedCollection }
        set {
            objectWillChange.send()
            selection = newValue
            checkedNames_ = []
            checkedCollection = nil
        }
    }

    private var selectedCollection: String {
        if let selection, state.store.collections[selection] != nil { return selection }
        return state.activeCollection
    }

    /// The ticks, but only while the collection they were made in is still the one showing.
    private var activeChecks: Set<String> {
        checkedCollection == selectedCollection ? checkedNames_ : []
    }

    /// Moves the window to a collection this model just created or renamed, keeping the ticks
    /// with it — unlike `selected`, which is the user picking a different collection.
    private func retarget(to name: String) {
        objectWillChange.send()
        if checkedCollection != nil { checkedCollection = name }
        selection = name
    }

    // MARK: - Panes

    public var items: [Item] {
        let active = state.activeCollection
        return state.collectionNames.map { name in
            Item(name: name, kind: state.kind(of: name), isActive: name == active,
                 isPublished: state.isPublished(name), hasPendingUpdate: state.pendingUpdates[name] != nil,
                 isLocated: state.isLocated(name))
        }
    }

    public var rows: [Row] {
        let collection = selectedCollection
        let locked = state.isSynced(collection)
        let checks = activeChecks
        let mcps = state.store.collections[collection]?.mcps ?? [:]
        // Ordinal, which is how the popover sorts the same connectors of the same collection:
        // two surfaces over one list must agree on its order.
        return mcps.keys.sorted().map { name in
            let entry = mcps[name]
            return Row(name: name, enabled: entry?.enabled ?? false,
                       caution: state.connectorCaution(name, in: collection), isLocked: locked,
                       checked: checks.contains(name),
                       typeText: CollectionsModel.typeText(of: entry?.config ?? .object([:])))
        }
    }

    /// The type column: the bridge the remote form recognises, or the launcher this connector
    /// runs, named the way it would be typed rather than by its full path.
    private static func typeText(of config: JSONValue) -> String {
        guard RemotePattern.detect(config) == nil else { return remoteType }
        return localType(launcherName(FormMapper.analyze(config).model.command))
    }

    /// The last component of a command, splitting on both separators rather than this platform's:
    /// a collection carries connectors authored on either, and a Windows command's launcher is
    /// still worth naming on a Mac. Split by hand, because the path APIs on the two platforms
    /// disagree about which separators count.
    private static func launcherName(_ command: String) -> String {
        command.split(whereSeparator: { $0 == "/" || $0 == "\\" }).last.map(String.init) ?? command
    }

    public var detailLine: String {
        let collection = selectedCollection
        if state.isSynced(collection) {
            guard let source = locatedSource(of: collection) else { return CollectionsModel.unlocatedDetail }
            return CollectionsModel.syncedDetail(source, syncStatus(of: collection))
        }
        var line = CollectionsModel.localDetail(state.store.collections[collection]?.mcps.count ?? 0)
        if collection == state.activeCollection { line += CollectionsModel.activeSuffix }
        // Only this machine's binding says where the document goes, so only this machine's window
        // says it publishes. Another machine's publish record is not a fact about this one.
        if let folder = state.collectionsCache.published[collection]?.folder {
            line += " · " + CollectionsModel.publishedDetail(folder)
        }
        return line
    }

    /// The synced document as this machine can name it, or nil when it cannot: not synced, no
    /// binding, or a sidecar entry that records neither a path nor a file name. The last of those
    /// takes a hand-edited or foreign collections file — every writer here sets a file name — but
    /// the decoder accepts one, and there is nothing to refresh or to report about a document
    /// nobody can point at.
    private func locatedSource(of collection: String) -> String? {
        guard state.isSynced(collection), state.isLocated(collection) else { return nil }
        let named = state.sourceBinding(of: collection)?.path
            ?? state.collectionsFile.collections[collection]?.fileName
        return (named?.isEmpty ?? true) ? nil : named
    }

    /// What the source is doing, in precedence order: what went wrong outranks what is waiting.
    private func syncStatus(of collection: String) -> String {
        if let failure = state.sourceErrors[collection] { return failure }
        if state.pendingUpdates[collection] != nil { return CollectionsModel.updateAvailableStatus }
        return CollectionsModel.upToDateStatus
    }

    // MARK: - Toolbar

    public var canExport: Bool { !state.isSynced(selectedCollection) && !activeChecks.isEmpty }

    /// Publishing a second time from the same machine is what the sheet's folder picker is for,
    /// so the toolbar offers it only to a collection this machine does not already publish.
    public var canPublish: Bool {
        let collection = selectedCollection
        return !state.isSynced(collection) && state.collectionsCache.published[collection] == nil
    }

    /// Refresh reads the bound document, so it needs one this machine can name.
    public var canRefresh: Bool { locatedSource(of: selectedCollection) != nil }

    public var canMakeLocalCopy: Bool { state.isSynced(selectedCollection) }

    public var canStopSyncing: Bool { state.isSynced(selectedCollection) }

    public var canStopPublishing: Bool { state.isPublished(selectedCollection) }

    /// The last local collection stays, because only a local one takes a new connector, and the
    /// last collection of any kind stays, because the store always has an active one. A synced
    /// collection is never the last local one, so only the second rule reaches it.
    public var canDelete: Bool {
        let collection = selectedCollection
        return state.isSynced(collection)
            ? state.collectionNames.count > 1
            : state.localCollectionNames.count > 1
    }

    /// Through the rows rather than the tick set, so a tick on a connector that has since
    /// vanished from the collection is dropped instead of exported.
    public var checkedNames: [String] { rows.filter(\.checked).map(\.name) }

    // MARK: - Rows

    /// A synced collection's rows cannot be exported, so they cannot be ticked either.
    public func setChecked(_ name: String, _ on: Bool) {
        let collection = selectedCollection
        guard !state.isSynced(collection) else { return }
        objectWillChange.send()
        if checkedCollection != collection {
            checkedNames_ = []
            checkedCollection = collection
        }
        if on { checkedNames_.insert(name) } else { checkedNames_.remove(name) }
    }

    /// The row switch, in the collection the window is showing rather than the active one.
    public func setEnabled(_ name: String, _ on: Bool) {
        state.setEnabled(name, on, in: selectedCollection)
        lastError = nil
    }

    /// The pencil: the same connector in two collections is two windows, so the target carries
    /// the collection this window is showing.
    public func editTarget(for row: String) -> EditTarget {
        let collection = selectedCollection
        let entry = state.store.collections[collection]?.mcps[row] ?? MCPEntry(config: .object([:]))
        return EditTarget.existing(name: row, entry: entry, in: collection)
    }

    /// The names the export sheet writes, in the order the rows show them.
    public func exportIntentForChecked() -> [String] { checkedNames }

    /// What the save panel opens with. The same name a published document would take, so an
    /// export and a publish of one collection cannot be told apart by their file names.
    public var suggestedExportFileName: String {
        Slug.make(selectedCollection) + "." + CollectionDocument.fileExtension
    }

    // MARK: - Collection actions

    public func create() {
        guard let typed = dialogs.promptForName(title: AppState.newCollectionTitle, initial: "") else { return }
        guard report(state.createCollection(named: typed)) else { return }
        retarget(to: typed.trimmingCharacters(in: .whitespaces))
    }

    public func rename() {
        let collection = selectedCollection
        guard let typed = dialogs.promptForName(title: AppState.renameCollectionTitle, initial: collection) else { return }
        guard report(state.renameCollection(collection, to: typed)) else { return }
        // The store trimmed the name the same way; following it keeps the window on the
        // collection the user just renamed rather than dropping back to the active one.
        retarget(to: typed.trimmingCharacters(in: .whitespaces))
    }

    public func delete() {
        let collection = selectedCollection
        guard dialogs.confirm(message: AppState.deleteCollectionMessage(collection), informative: nil,
                              primary: AppState.deleteButton, destructive: true) else { return }
        // The store refuses to delete the last local collection, and the last one of any kind.
        // Asked here as well as in the toolbar, so a refusal cannot arrive after the publishing
        // below has already stopped. The store reports it: it refuses before it touches anything,
        // so asking it early is a no-op that still produces the right message.
        guard canDelete else { report(state.deleteCollection(named: collection)); return }
        if let fileName = publishedFileName(of: collection) {
            state.stopPublishing(collection, deleteFile: askAboutPublishedFile(fileName))
        }
        guard report(state.deleteCollection(named: collection)) else { return }
        selected = nil   // back to the active collection
    }

    /// Stop Publishing: the collection stays, and only the document in the folder is in question.
    public func stopPublishing() {
        let collection = selectedCollection
        guard state.isPublished(collection) else { return }
        // Nothing on this machine writes the document when there is no binding for it, so there
        // is no file here to offer to remove.
        let deleteFile = publishedFileName(of: collection).map(askAboutPublishedFile) ?? false
        state.stopPublishing(collection, deleteFile: deleteFile)
        lastError = nil
    }

    /// Stop Syncing keeps every connector, every filled value and every switch, so there is
    /// nothing to warn about and nothing to confirm.
    public func stopSyncing() {
        state.stopSyncing(selectedCollection)
        lastError = nil
    }

    public func refresh() {
        state.refreshSource(for: selectedCollection)
        lastError = nil
    }

    /// The whole synced collection again as a local one the user can edit.
    public func makeLocalCopy() {
        let collection = selectedCollection
        guard state.isSynced(collection) else { return }
        guard let typed = dialogs.promptForName(title: AppState.newCollectionTitle, initial: collection) else { return }
        guard report(state.makeLocalCopyOfCollection(collection, named: typed)) else { return }
        retarget(to: typed.trimmingCharacters(in: .whitespaces))
    }

    public func switchTo(_ name: String) {
        state.switchCollection(to: name)
        lastError = nil
    }

    // MARK: - Helpers

    /// The document this machine writes for `collection`, or nil when nothing here publishes it.
    private func publishedFileName(of collection: String) -> String? {
        guard state.collectionsCache.published[collection] != nil,
              let record = state.collectionsFile.collections[collection]?.publish else { return nil }
        return record.slug + "." + CollectionDocument.fileExtension
    }

    /// Default no: the view's default button is Keep, and this model only records the answer.
    private func askAboutPublishedFile(_ fileName: String) -> Bool {
        dialogs.confirm(message: CollectionsModel.deletePublishedFileQuestion(fileName), informative: nil,
                        primary: CollectionsModel.removeFileButton, cancel: CollectionsModel.keepFileButton,
                        destructive: false)
    }

    @discardableResult
    private func report(_ error: String?) -> Bool {
        lastError = error
        return error == nil
    }

    /// Stops listening to AppState. The app does not call this: the subscriptions hold `self`
    /// weakly and die with the `@StateObject`. Tests call it to prove the relays are what repaint
    /// the window.
    public func dispose() { subscriptions.removeAll() }
}

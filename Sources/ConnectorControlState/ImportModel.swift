import Combine
import Foundation
import ConnectorControlCore

/// The Import sheet: one collection document, and the two exclusive things that can be done with
/// it — copies into a collection the user owns, or a collection of its own that stays in sync
/// with the file. Every row says what would happen to that connector before anything happens,
/// because an import lands commands Claude will run.
///
/// Mirror: windows/src/ConnectorControl.Core/State/ImportModel.cs
@MainActor
public final class ImportModel: ObservableObject {
    public static let title = "Import connectors"
    public static let addModeDetail = "Copies. No link to the file afterwards."
    public static let syncModeTitle = "Keep as its own collection, in sync with this file"
    public static let syncModeDetail = "Read-only except your secrets and switches. Changes at the source arrive for review."
    public static let newBadge = "new · arrives off"
    public static let presentBadge = "already present · skipped"
    public static let replaceKeepsValues = "keeps your filled values"
    public static let addTitle = "Add"
    public static let replaceTitle = "Replace"
    public static let keepBothTitle = "Keep both"
    public static let skipTitle = "Skip"
    /// Stands in for the document's `author` when it travelled without one.
    public static let unknownAuthor = "unknown author"
    public static let cancelButton = "Cancel"
    /// A screen reader's name for the name field in sync mode, which carries no label of its
    /// own. The mockup draws "Name:" beside it; this is the longer form a reader needs without
    /// the sentence above the field for context.
    public static let syncNameLabel = "Collection name"

    public static func addModeTitle(_ collection: String) -> String { "Add to a collection: \(collection)" }

    public static func sourceLine(_ document: String, _ author: String, _ connectors: Int) -> String {
        "\u{201C}\(document)\u{201D} by \(author) · \(connectors) connectors"
    }

    public static func skippedBadge(_ reason: String) -> String { "skipped: \(reason)" }

    /// A screen reader's name for the bare tick beside a connector.
    public static func includeLabel(_ connector: String) -> String { "Include \(connector)" }

    /// A screen reader's name for a collision's choice picker. Without one it reads out the
    /// badge beside it, which says the row is skipped — the opposite of what the picker is for.
    public static func collisionPickerLabel(_ connector: String) -> String { "What to do with \(connector)" }

    public static func importButton(_ count: Int) -> String { "Import \(count)" }

    /// What a collision offers, in the order the row's picker lists them. `add` is not among
    /// them: a row with nothing in its way shows `newBadge` instead of a picker, so the view
    /// binds this list rather than deciding for itself which cases a collision has.
    public static let collisionChoices: [ImportChoice] = [.replace, .keepBoth, .skip]

    /// A row's badge, in precedence order: a connector this platform cannot run says so first,
    /// then one the target already holds, then a new arrival.
    private static func badge(excludedReason: String?, present: Bool) -> String {
        if let excludedReason { return skippedBadge(excludedReason) }
        return present ? presentBadge : newBadge
    }

    /// One choice's picker label. Total over the enum, so a row's picker needs no logic of its
    /// own; `add` is the answer for a name nothing here already holds.
    public static func choiceTitle(_ choice: ImportChoice) -> String {
        switch choice {
        case .add: return addTitle
        case .replace: return replaceTitle
        case .keepBoth: return keepBothTitle
        case .skip: return skipTitle
        }
    }

    public enum Mode: Equatable, Sendable { case addToCollection, keepInSync }

    /// Where the copies land, as the target picker lists it: an existing local collection, or a
    /// new one named when it is chosen and made only when Import is pressed.
    public enum Target: Hashable, Sendable { case collection(String), newCollection }

    /// One target's picker label: the collection's name, or New Collection, the words Copy to's
    /// menu ends with for the same thing.
    public static func targetTitle(_ target: Target) -> String {
        switch target {
        case .collection(let name): return name
        case .newCollection: return CollectionsModel.newButton
        }
    }

    /// One connector of the document against the collection it would land in. A tick or a choice
    /// here republishes the whole `rows` array, which is what makes `importCount` and the button
    /// re-read; the Windows mirror has to raise PropertyChanged from the row for the same effect.
    ///
    /// `present` is what the badge says and what `choice` answers; an excluded connector cannot be
    /// included at all, since this platform has no way to run it. A struct the sheet edits through
    /// its index, as the publish rows are; the Windows mirror is a class, because WPF's two-way
    /// bindings need a row that stays put.
    public struct Row: Identifiable, Equatable {
        public let id: String
        public let name: String
        public var include: Bool
        public let present: Bool
        public var choice: ImportChoice
        public let excludedReason: String?
        public let needs: [String]
        /// The caution glyph's tooltip, or nil for no glyph — the same sentence a connector of
        /// the collection this row lands in already carries for the same condition, rather than
        /// a second wording of it. Filled by the model, as `CollectionsModel.Row.caution` is,
        /// because the sentence belongs to AppState and a row is not on its actor.
        public let needsCaution: String?
        /// What the row says about itself beside its name: nothing this platform can run, a name
        /// the target already holds, or a new arrival. Built where the row is, so the view binds
        /// one string instead of choosing between a constant and a factory.
        public let badge: String

        /// Whether the row shows the collision picker rather than its badge: a name the target
        /// holds, coming across. Unticking a collision and choosing Skip mean the same, so an
        /// unticked one shows its badge instead. A collision preselects Replace, should it be
        /// ticked; the Copy sheet's rows preselect Keep both.
        public var showsPicker: Bool { present && include }

        /// Whether the row can be ticked at all: a connector this platform has no way to run
        /// carries the reason it cannot, and there is nothing about it left to decide.
        public var canInclude: Bool { excludedReason == nil }

        public init(name: String, include: Bool, present: Bool, choice: ImportChoice,
                    excludedReason: String?, needs: [String], needsCaution: String?, badge: String) {
            self.id = name
            self.name = name
            self.include = include
            self.present = present
            self.choice = choice
            self.excludedReason = excludedReason
            self.needs = needs
            self.needsCaution = needsCaution
            self.badge = badge
        }
    }

    public let path: String
    /// The document's own name, or the file's when it could not be read.
    public private(set) var documentName: String
    public private(set) var author: String?
    /// Why this document cannot be imported at all, nil when it read. The Windows mirror also
    /// carries `HasLoadError`: XAML cannot bind a body's visibility to "this optional is not
    /// nil", where SwiftUI binds the optional itself. The sheet shows it in
    /// place of the rows and Import stays out of reach.
    public private(set) var loadError: String?

    /// The Windows mirror calls this `ImportMode`: there the nested `Mode` enum already owns the
    /// name, the same collision `PublishModel.SheetTitle` has.
    @Published public var mode: Mode = .addToCollection
    /// Which collection the copies land in. Changing it rebuilds the rows: a different target
    /// collides with different connectors, so the badges and the ticks have to follow it.
    @Published public var targetCollection: String {
        didSet { if targetCollection != oldValue { rebuildRows() } }
    }
    /// The name New Collection was given, while it is the target; nil while an existing collection
    /// is. Nothing by this name exists until `perform()` makes it.
    @Published public private(set) var newCollectionName: String?
    @Published public var syncName: String
    @Published public var rows: [Row] = []

    private let state: AppState
    private let rendered: RenderedCollection?

    /// `selected` is the collection the Collections window is showing, which is the target when
    /// it is local; otherwise, or with nothing selected, the active collection is, as it was
    /// before the window had a selection to offer.
    public init(state: AppState, path: String, selected: String? = nil) {
        self.state = state
        self.path = path
        let url = URL(fileURLWithPath: path).standardizedFileURL
        let (document, _, failure) = AppState.readDocument(at: url)
        // The target picker is filled either way, so a sheet that cannot read its document still
        // shows the collection the user was in.
        let locals = state.localCollectionNames
        if let selected, locals.contains(selected) {
            targetCollection = selected
        } else {
            targetCollection = locals.contains(state.activeCollection) ? state.activeCollection : (locals.first ?? "")
        }
        guard let document else {
            loadError = failure
            documentName = url.lastPathComponent
            author = nil
            rendered = nil
            syncName = ""
            return
        }
        documentName = document.name
        author = document.author
        rendered = document.render()
        // The document's name, suffixed until it is one no collection here already answers to.
        syncName = ImportModel.freeName(document.name, taken: Set(state.collectionNames))
        rebuildRows()
    }

    /// "“Data team” by Acme Data Platform · 4 connectors" — what the file says about itself. The
    /// Windows mirror calls it `SourceSentence`, where the static factory already owns the name.
    public var sourceLine: String {
        ImportModel.sourceLine(documentName, author ?? ImportModel.unknownAuthor, rows.count)
    }

    /// The collections copies may land in. A synced collection answers to its own document, so
    /// it is never one of them.
    public var localCollections: [String] { state.localCollectionNames }

    /// What the target picker lists: every local collection, then New Collection.
    public var targets: [Target] { localCollections.map(Target.collection) + [.newCollection] }

    /// The picker's selection. Choosing New Collection asks for its name through AppState's
    /// dialogs, as Copy to ▸ New Collection does; a cancelled prompt leaves the target as it was.
    /// Choosing a collection gives up a new one that was named.
    public var target: Target {
        get { newCollectionName == nil ? .collection(targetCollection) : .newCollection }
        set {
            switch newValue {
            case .collection(let name):
                let wasNew = newCollectionName != nil
                newCollectionName = nil
                if name != targetCollection { targetCollection = name } else if wasNew { rebuildRows() }
            case .newCollection:
                guard let typed = state.dialogs.promptForName(title: AppState.newCollectionTitle, initial: "") else {
                    // The picker already shows the choice it just made; this puts it back.
                    objectWillChange.send()
                    return
                }
                newCollectionName = MasterStore.collectionName(typed)
                rebuildRows()
            }
        }
    }

    /// The collection the copies land in, by name, whichever kind of target it is: what the mode's
    /// title says.
    public var targetName: String { newCollectionName ?? targetCollection }

    /// What the Import button counts: the rows that are ticked in add mode, and everything this
    /// platform can carry in sync mode, where the whole document comes across or none of it.
    public var importCount: Int {
        switch mode {
        // A ticked row set to Skip lands nothing, so the button must not promise it: `perform`
        // sends `.skip` for exactly these, and a count that disagreed would say "Import 3" over
        // two connectors arriving.
        case .addToCollection:
            return rows.filter { $0.include && $0.excludedReason == nil && $0.choice != .skip }.count
        case .keepInSync: return rows.filter { $0.excludedReason == nil }.count
        }
    }

    public var canImport: Bool {
        guard loadError == nil else { return false }
        switch mode {
        case .addToCollection:
            return importCount > 0 && !targetName.isEmpty
        case .keepInSync:
            // A document every connector of which this platform excludes still subscribes: what
            // the author ships next may be something this machine can run.
            return !syncName.trimmingCharacters(in: .whitespaces).isEmpty
        }
    }

    /// Lands what the sheet says: copies into the target, or a collection of its own bound to
    /// the file. nil on success, else the message to show. A new collection is made here, empty,
    /// local and not active (`AppState.addEmptyCollection`), and the copies go into it.
    public func perform() -> String? {
        if let loadError { return loadError }
        switch mode {
        case .addToCollection:
            var choices: [String: ImportChoice] = [:]
            for row in rows {
                choices[row.name] = row.include && row.excludedReason == nil ? row.choice : .skip
            }
            if let name = newCollectionName {
                if let error = state.addEmptyCollection(named: name) { return error }
                return state.importCopies(documentAt: path, into: name, choices: choices)
            }
            return state.importCopies(documentAt: path, into: targetCollection, choices: choices)
        case .keepInSync:
            return state.subscribe(documentAt: path, as: syncName)
        }
    }

    /// One row per connector the document carries, including the ones this platform excludes —
    /// a connector that cannot come across is worth seeing and saying why. Ticked by default
    /// unless something already answers to that name in the target, which is the one case where
    /// importing takes a decision from the user.
    private func rebuildRows() {
        guard let rendered else {
            rows = []
            return
        }
        // A new collection holds nothing yet, whatever an existing one of that name might.
        let held = newCollectionName == nil ? state.store.collections[targetCollection]?.mcps ?? [:] : [:]
        rows = Set(rendered.connectors.keys).union(rendered.excluded.keys).sorted(by: { $0.ordinallyPrecedes($1) }).map { name in
            let reason = rendered.excluded[name]
            let present = held[name] != nil
            // First appearance in the config the import would write, not the `needs` map's own
            // order: the map is unordered, and reading the markers is what makes this row's
            // sentence the one the connector will carry once it has landed.
            let needs = rendered.connectors[name].map { Placeholder.unfilledNames(in: $0.config) } ?? []
            return Row(name: name, include: reason == nil && !present, present: present,
                       choice: present ? .replace : .add, excludedReason: reason, needs: needs,
                       needsCaution: needs.isEmpty
                           ? nil : AppState.needsValueCaution(needs.joined(separator: ", ")),
                       badge: ImportModel.badge(excludedReason: reason, present: present))
        }
    }

    /// `name`, or "name 2", "name 3", … — the first one nothing in `taken` answers to.
    private static func freeName(_ name: String, taken: Set<String>) -> String {
        guard taken.contains(name) else { return name }
        var suffix = 2
        while taken.contains("\(name) \(suffix)") { suffix += 1 }
        return "\(name) \(suffix)"
    }
}

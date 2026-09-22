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

    public static func addModeTitle(_ collection: String) -> String { "Add to a collection: \(collection)" }

    public static func sourceLine(_ document: String, _ author: String, _ connectors: Int) -> String {
        "\u{201C}\(document)\u{201D} by \(author) · \(connectors) connectors"
    }

    public static func skippedBadge(_ reason: String) -> String { "skipped: \(reason)" }

    /// The caution glyph's tooltip on a row the author left values to fill in, naming them in
    /// the order the row lists them.
    public static func needsTooltip(_ names: [String]) -> String {
        "Needs your values: \(names.joined(separator: ", "))"
    }

    public static func importButton(_ count: Int) -> String { "Import \(count)" }

    /// What a collision offers, in the order the row's picker lists them. `add` is not among
    /// them: a row with nothing in its way shows `newBadge` instead of a picker, so the view
    /// binds this list rather than deciding for itself which cases a collision has.
    public static let collisionChoices: [ImportChoice] = [.replace, .keepBoth, .skip]

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

    /// One connector of the document against the collection it would land in. `present` is what
    /// the badge says and what `choice` answers; an excluded connector cannot be included at all,
    /// since this platform has no way to run it. A struct the sheet edits through its index, as
    /// the publish rows are; the Windows mirror is a class, because WPF's two-way bindings need
    /// a row that stays put.
    public struct Row: Identifiable, Equatable {
        public let id: String
        public let name: String
        public var include: Bool
        public let present: Bool
        public var choice: ImportChoice
        public let excludedReason: String?
        public let needs: [String]

        public init(name: String, include: Bool, present: Bool, choice: ImportChoice,
                    excludedReason: String?, needs: [String]) {
            self.id = name
            self.name = name
            self.include = include
            self.present = present
            self.choice = choice
            self.excludedReason = excludedReason
            self.needs = needs
        }
    }

    public let path: String
    /// The document's own name, or the file's when it could not be read.
    public private(set) var documentName: String
    public private(set) var author: String?
    /// Why this document cannot be imported at all, nil when it read. The sheet shows it in
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
    @Published public var syncName: String
    @Published public var rows: [Row] = []

    private let state: AppState
    private let rendered: RenderedCollection?

    public init(state: AppState, path: String) {
        self.state = state
        self.path = path
        let url = URL(fileURLWithPath: path).standardizedFileURL
        let (document, _, failure) = AppState.readDocument(at: url)
        // The target picker is filled either way, so a sheet that cannot read its document still
        // shows the collection the user was in.
        let locals = state.localCollectionNames
        targetCollection = locals.contains(state.activeCollection) ? state.activeCollection : (locals.first ?? "")
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

    /// What the Import button counts: the rows that are ticked in add mode, and everything this
    /// platform can carry in sync mode, where the whole document comes across or none of it.
    public var importCount: Int {
        switch mode {
        case .addToCollection: return rows.filter { $0.include && $0.excludedReason == nil }.count
        case .keepInSync: return rows.filter { $0.excludedReason == nil }.count
        }
    }

    public var canImport: Bool {
        guard loadError == nil else { return false }
        switch mode {
        case .addToCollection:
            return importCount > 0 && !targetCollection.isEmpty
        case .keepInSync:
            // A document every connector of which this platform excludes still subscribes: what
            // the author ships next may be something this machine can run.
            return !syncName.trimmingCharacters(in: .whitespaces).isEmpty
        }
    }

    /// Lands what the sheet says: copies into the target, or a collection of its own bound to
    /// the file. nil on success, else the message to show.
    public func perform() -> String? {
        if let loadError { return loadError }
        switch mode {
        case .addToCollection:
            var choices: [String: ImportChoice] = [:]
            for row in rows {
                choices[row.name] = row.include && row.excludedReason == nil ? row.choice : .skip
            }
            return state.importCopies(documentAt: path, into: targetCollection, choices: choices, date: state.today)
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
        let held = state.store.collections[targetCollection]?.mcps ?? [:]
        rows = Set(rendered.connectors.keys).union(rendered.excluded.keys).sorted().map { name in
            let reason = rendered.excluded[name]
            let present = held[name] != nil
            return Row(name: name, include: reason == nil && !present, present: present,
                       choice: present ? .replace : .add, excludedReason: reason,
                       needs: rendered.connectors[name]?.needs.keys.sorted() ?? [])
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

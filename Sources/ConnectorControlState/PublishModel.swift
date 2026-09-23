import Combine
import Foundation
import ConnectorControlCore

/// The Publish/Export sheet: every environment value this collection carries and every argument
/// that looks like a path on this machine, with the ticks that decide what travels as a value and
/// what travels as a placeholder. The preview under them is the document itself, because the
/// only guarantee worth making about a secret is that the author saw every byte that leaves.
///
/// Mirror: windows/src/ConnectorControl.Core/State/PublishModel.cs
@MainActor
public final class PublishModel: ObservableObject {
    public static let envSectionTitle = "Environment values · stripped unless shared"
    public static let pathsSectionTitle = "Machine-specific paths · found in arguments"
    public static let previewTitle = "Document preview"
    public static let shareValueLabel = "share value"
    public static let hintPlaceholder = "hint for recipients"
    public static let pathNamePlaceholder = "placeholder name"
    public static let publishButton = "Publish"
    public static let exportButton = "Export"
    public static let cancelButton = "Cancel"
    /// This sheet's own folder picker, not the failed-publish banner's button of the same words:
    /// a sheet's buttons are its model's, as Settings' and the Import sheet's already are.
    public static let chooseFolderButton = "Choose Folder"
    /// What a screen reader says for the bare tick beside a path row, which has no visible label.
    public static let markPathLabel = "Mark as a path this machine supplies"
    /// The button beside an unresolved mark's note: drop the mark and let the path travel as the
    /// preview shows it.
    public static let forgetMarkButton = "Forget Mark"
    /// The button beside a kept path's note: let that path travel as written in this collection's
    /// document, which the preview above shows.
    public static let releaseValueButton = "Release"
    /// The button beside a publish folder's note: write `${COLLECTION_DIR}` in that place of the
    /// connector, as the author's own edit.
    public static let useDirectoryTokenButton = "Use ${COLLECTION_DIR}"

    public static func title(_ collection: String) -> String { "Publish “\(collection)”" }

    /// The sheet's own title in export mode. No trailing ellipsis: the one on the menu item that
    /// opens it (`PopoverModel.exportTitleFor`) says a sheet follows, and this is that sheet.
    public static func exportTitle(_ collection: String) -> String { "Export “\(collection)”" }

    /// "this Mac" is the platform-forced half of this sentence; the Windows mirror says "this PC".
    public static func folderLine(_ fileName: String) -> String { "writes \(fileName) from this Mac on every change" }

    public static func warningLine(_ connector: String, _ warning: String) -> String { "\(connector): \(warning)" }

    public static func footerLine(_ fileName: String, _ originShort: String) -> String { "\(fileName) · \(originShort)" }

    public static func unresolvedMarkNote(_ connector: String, _ name: String) -> String { "A path marked “\(name)” in “\(connector)” has moved. Tick it where it now sits, or forget the mark." }

    public static func keptPathNote(_ connector: String, _ field: String) -> String { "“\(connector)” carries a path this machine keeps back, in \(field). Tick it where it sits, or release it." }

    public static func publishFolderNote(_ connector: String, _ field: String) -> String { "“\(connector)” carries this machine's publish folder as written, in \(field). Use ${COLLECTION_DIR} in its place." }

    /// The folder sits where this sheet cannot write: the author's editor is the way out.
    public static func publishFolderEditNote(_ connector: String, _ field: String) -> String { "“\(connector)” carries this machine's publish folder as written, in \(field), which this sheet cannot write over. Open “\(connector)” and write ${COLLECTION_DIR} there." }

    /// Another collection's folder, or a synced collection's: whose it is, since releasing it
    /// sends one of this machine's own folders.
    public static func otherFolderNote(_ connector: String, _ field: String, _ collection: String) -> String { "“\(connector)” carries, in \(field), the folder this machine keeps “\(collection)” in. Tick it where it sits, or release it." }

    /// A path mark that lost its argument: its text is held by no argument now. It waits for the
    /// author to tick the path where it now sits, which answers it, or to forget it.
    public struct UnresolvedMark: Identifiable, Equatable {
        public let id: String
        public let connector: String
        public let name: String
        public let hint: String?
        let pointer: JSONPointer
    }

    /// A path this machine keeps back that the document would carry as written, and where.
    public struct KeptPath: Identifiable, Equatable {
        public var id: String { connector + "\u{0}" + field + "\u{0}" + value }
        public let value: String
        public let connector: String
        /// Its place in the connector's document form, as the preview shows it: `local.command`,
        /// `local.args[1]`, `env.NAME.value`, `env.NAME.hint`, `needs.NAME.hint`, `additional.cwd`,
        /// `remote.extraArgs[0]`.
        public let field: String
        /// Which answer the entry takes.
        public let kind: Kind

        public enum Kind: Equatable {
            /// A path this machine keeps back: ticked where it sits in an argument row, or released
            /// with `releaseKeptPath`.
            case path
            /// A folder of this collection's own, which `${COLLECTION_DIR}` stands for, in a place
            /// the token can be written: answered with `useDirectoryToken`.
            case folder
        }

        public init(value: String, connector: String, field: String, kind: Kind = .path) {
            self.value = value
            self.connector = connector
            self.field = field
            self.kind = kind
        }
    }

    /// One environment variable of one connector. Stripped by default: its name and hint travel,
    /// its value does not. A struct the sheet edits through its index, as every other row here
    /// is; the Windows mirror is a class, because WPF's two-way bindings need a row that stays put.
    public struct EnvRow: Identifiable, Equatable {
        public let id: String
        public let connector: String
        public let name: String
        /// What the variable holds now, in full and unelided, so the tick beside it is a decision
        /// made with the value in view. Shortening it is the sheet's business, not the model's.
        public let value: String
        public var share: Bool
        public var hint: String

        public init(connector: String, name: String, value: String, share: Bool, hint: String) {
            self.id = connector + "/env/" + name
            self.connector = connector
            self.name = name
            self.value = value
            self.share = share
            self.hint = hint
        }
    }

    /// One argument that looks like a path on this machine. Marking it replaces it with a
    /// placeholder every recipient fills in for themselves.
    public struct PathRow: Identifiable, Equatable {
        public let id: String
        public let connector: String
        public let pointer: JSONPointer
        public let value: String
        public var marked: Bool
        public var name: String
        public var hint: String

        public init(connector: String, pointer: JSONPointer, value: String, marked: Bool, name: String, hint: String) {
            self.id = connector + pointer.description
            self.connector = connector
            self.pointer = pointer
            self.value = value
            self.marked = marked
            self.name = name
            self.hint = hint
        }
    }

    public let collection: String
    /// The connectors this sheet speaks for: the Export sheet's ticked subset, or nil for the
    /// whole collection. Publishing always writes the whole collection, so a model built with a
    /// subset is an export's — `publish()` on one would record an intent that speaks for only
    /// part of what the document carries.
    public let connectors: [String]?
    /// Where the document is written, nil until the user chooses. Settable: the sheet's Choose
    /// Folder… is the only thing that fills it.
    @Published public var folder: String?
    @Published public var envRows: [EnvRow]
    @Published public var pathRows: [PathRow] {
        // A tick made since the sheet opened answers the next lost mark of its connector and takes
        // its name and hint. Guarded: the assignment writes the rows again, and a published
        // property's own observer would otherwise run for ever.
        didSet {
            guard !answering else { return }
            answering = true
            defer { answering = false }
            answerLostMarks(since: oldValue)
        }
    }

    private let state: AppState
    /// Every path mark the sheet found lost on open, each on its own: its text is held by no
    /// argument of its connector, or the connector is gone. Kept rather than dropped, because the
    /// rows alone would show such a path unticked and publishing them would send it as written.
    /// In connector, then pointer, order.
    private let lostMarks: [UnresolvedMark]
    /// The lost marks the author chose to forget.
    @Published private var forgotten: Set<String> = []
    /// Row → the lost mark its tick answers.
    private var answers: [String: String] = [:]
    /// The paths the author released in this sheet, to travel as written in this document.
    @Published private var released: Set<String> = []
    /// The path rows ticked when the sheet opened. Those are marks already on record, so only a
    /// tick made since can stand in for a lost one.
    private let tickedAtOpen: Set<String>
    /// Each row's name as the sheet opened it, so a tick that answers a lost mark gives it that
    /// mark's name only while the author has not typed one of their own.
    private let namesAtOpen: [String: String]
    private var answering = false

    public init(state: AppState, collection: String, connectors: [String]? = nil) {
        self.state = state
        self.collection = collection
        self.connectors = connectors
        // A collection that already publishes reopens showing what it publishes: the folder it
        // writes to and every tick the record remembers.
        let intent = state.collectionsFile.collections[collection]?.publish?.intent ?? .none
        folder = state.collectionsCache.published[collection]?.folder
        var env: [EnvRow] = []
        var paths: [PathRow] = []
        var lost: [UnresolvedMark] = []
        // What this machine keeps back from the collection's document: its lists of marked paths
        // and the folders it binds. A row holding one of them starts ticked even when the record
        // no longer marks it — the other machine may have dropped the mark while this one still
        // sends the path — so the author unticks it on purpose, in view of the preview, or it
        // stays a placeholder.
        let denied = Set(state.keptBack(for: collection).values.map(KeptValue.nfc))
        let held = PublishModel.held(in: state, collection, only: connectors)
        for name in held.keys.sorted(by: { $0.ordinallyPrecedes($1) }) {
            guard let config = held[name]?.config else { continue }
            let shared = intent.shareValues[name] ?? []
            let hints = intent.hints[name] ?? [:]
            let variables = PublishModel.env(of: config)
            for key in variables.keys.sorted() {
                env.append(EnvRow(connector: name, name: key, value: variables[key] ?? "",
                                  share: shared.contains(key), hint: hints[key] ?? ""))
            }
            var found = 0
            let arguments = PublishModel.arguments(of: config)
            // The ticks sit where the exporter would place them, not where the record says they
            // were made: an argument that moved since keeps its tick. Every other argument holding
            // a marked path's text is ticked with that mark's name and hint too, since a copy of
            // a marked path is that path. A marked argument keeps its row even once it stops
            // looking like a path (the file it named is gone), or publishing from the sheet would
            // quietly unmark it.
            let marks = intent.pathMarks[name] ?? [:]
            let placement = PublishIntent.placePathMarks(marks, in: arguments)
            let byValue = PublishModel.marksByValue(marks)
            // A mark whose text no argument holds any more has nothing to tick: it waits, on its
            // own, for the author to tick the path where it now sits.
            let texts = Set(arguments.map(KeptValue.nfc))
            lost += PublishModel.sortedByPointer(placement.unresolved)
                .filter { $0.value.value.map { !texts.contains(KeptValue.nfc($0)) } ?? false }
                .map { UnresolvedMark(id: name + $0.key.description, connector: name,
                                      name: $0.value.name, hint: $0.value.hint, pointer: $0.key) }
            for (index, argument) in arguments.enumerated() {
                let mark = placement.placed[index] ?? byValue[KeptValue.nfc(argument)]
                let ticked = mark != nil || denied.contains(KeptValue.nfc(argument))
                guard PublishModel.looksLikeAPath(argument) || ticked else { continue }
                found += 1
                paths.append(PathRow(connector: name, pointer: JSONPointer(["args", String(index)]), value: argument,
                                     marked: ticked,
                                     name: mark?.name ?? PublishModel.defaultPathName(found),
                                     hint: mark?.hint ?? ""))
            }
        }
        // A mark for a connector the collection no longer holds was made on one renamed or
        // removed where the record could not follow, and the exporter refuses it whatever the
        // subset. No row can be ticked for it, so each waits to be forgotten.
        let all = state.store.collections[collection]?.mcps ?? [:]
        for name in intent.pathMarks.keys.sorted(by: { $0.ordinallyPrecedes($1) }) where all[name] == nil {
            lost += PublishModel.sortedByPointer(intent.pathMarks[name] ?? [:]).filter { $0.value.value != nil }
                .map { UnresolvedMark(id: name + $0.key.description, connector: name,
                                      name: $0.value.name, hint: $0.value.hint, pointer: $0.key) }
        }
        envRows = env
        pathRows = paths
        lostMarks = lost.sorted { a, b in
            a.connector != b.connector ? a.connector.ordinallyPrecedes(b.connector)
                : a.pointer.description.ordinallyPrecedes(b.pointer.description)
        }
        tickedAtOpen = Set(paths.filter(\.marked).map(\.id))
        namesAtOpen = Dictionary(uniqueKeysWithValues: paths.map { ($0.id, $0.name) })
    }

    public var title: String { PublishModel.title(collection) }

    /// The document's name in the folder: the slug publishing fixed, or what this collection's
    /// name would make of it.
    public var fileName: String {
        (state.collectionsFile.collections[collection]?.publish?.slug ?? Slug.make(collection))
            + "." + CollectionDocument.fileExtension
    }

    public var folderLine: String { PublishModel.folderLine(fileName) }

    /// Whether each section has anything to show. A collection of remote connectors with no
    /// passthrough environment has neither, and an empty heading over nothing is worse than no
    /// heading; the sheet binds these rather than counting rows itself.
    public var hasEnvRows: Bool { !envRows.isEmpty }

    public var hasPathRows: Bool { !pathRows.isEmpty }

    /// The eight characters of the origin the footer shows — enough to tell one publisher's
    /// document from another's at a glance, which is all the footer is for. Empty until the
    /// collection has published once, because that is when the origin is minted. Read from the
    /// publish record rather than through `exportDocument`, which carries the same value and
    /// renders every connector to get there.
    public var originShort: String {
        String((state.collectionsFile.collections[collection]?.publish?.origin ?? "").prefix(8))
    }

    /// The footer: the file name, and the origin once there is one. The Windows mirror calls this
    /// `FooterSentence`, where the static factory already owns the name.
    public var footerLine: String {
        let origin = originShort
        return origin.isEmpty ? fileName : PublishModel.footerLine(fileName, origin)
    }

    /// Nothing is published while a mark is unresolved or a kept path unanswered: the rows would
    /// send the path as written.
    public var canPublish: Bool { !(folder ?? "").isEmpty && unresolvedMarks.isEmpty && keptPaths.isEmpty }

    /// Nothing is exported while either waits, for the same reason.
    public var canExport: Bool { unresolvedMarks.isEmpty && keptPaths.isEmpty }

    /// The lost marks still unanswered, one note each, in connector then pointer order. A lost
    /// mark is answered by its own tick — a path row of its connector ticked since the sheet
    /// opened, with a name the placeholder can carry, each tick answering the next lost mark in
    /// pointer order — or by forgetting it. Unticking the row puts it back.
    public var unresolvedMarks: [UnresolvedMark] {
        let answered = Set(pathRows.compactMap { row in
            row.marked && !PublishModel.placeholderName(row.name).isEmpty ? answers[row.id] : nil
        })
        return lostMarks.filter { !forgotten.contains($0.id) && !answered.contains($0.id) }
    }

    /// Drops one lost mark by the author's explicit choice: its path then travels as the preview
    /// shows it, as written unless a row of it is ticked.
    public func forgetUnresolvedMark(_ id: String) {
        forgotten.insert(id)
        answers = answers.filter { $0.value != id }
    }

    /// Every path this machine keeps back that the document, as the rows now make it, would carry
    /// as written, with its connector and field: a copy of a ticked path; a path on one of this
    /// machine's lists of marked paths, this collection's or another's; a folder it binds. Each
    /// is answered by ticking it where it sits in an argument row, or by releasing it.
    public var keptPaths: [KeptPath] {
        let held = PublishModel.held(in: state, collection, only: connectors).mapValues(\.config)
        var found = CollectionDocument.copiesOfMarkedPaths(in: held, intent: intent)
            .map { KeptPath(value: $0.value, connector: $0.connector, field: $0.field) }
        if found.isEmpty, let document = try? state.exportDocument(for: collection, intent: intent, only: connectors) {
            let kept = state.keptBack(for: collection, reviewed: reviewedValues, released: released)
            found = document.findings(of: kept.values)
                .map { KeptPath(value: $0.value, connector: $0.connector, field: $0.field) }
                + document.findings(of: kept.folders).map {
                    // A folder of this collection's own is a folder entry wherever it sits, even
                    // where the rewrite cannot reach it: its note then says where to write the token.
                    KeptPath(value: $0.value, connector: $0.connector, field: $0.field, kind: .folder)
                }
        }
        var seen: Set<String> = []
        return found.filter { seen.insert($0.id).inserted }
    }

    /// What the sheet says about one entry: how it is answered, and for a path kept back that is
    /// another collection's folder, whose folder it is. The view shows this rather than composing
    /// it, so a new kind of entry cannot reach the sheet with the wrong sentence.
    public func note(for kept: KeptPath) -> String {
        let field = state.fieldName(of: KeptValueFinding(connector: kept.connector, field: kept.field, value: kept.value),
                                    in: collection)
        switch kept.kind {
        case .folder:
            return canWriteDirectoryToken(connector: kept.connector, field: kept.field, folder: kept.value)
                ? PublishModel.publishFolderNote(kept.connector, field)
                : PublishModel.publishFolderEditNote(kept.connector, field)
        case .path:
            if let owner = state.collectionBound(to: kept.value) {
                return PublishModel.otherFolderNote(kept.connector, field, owner)
            }
            return PublishModel.keptPathNote(kept.connector, field)
        }
    }

    /// Writes `${COLLECTION_DIR}` where `kept`, a folder of this collection's own, sits: in the
    /// sheet's own hint for a hint, and otherwise in the connector itself, saved as an editor
    /// save is and applied to Claude's config when the collection is the active one. The author's
    /// own edit, in view of the preview. nil when the token is written; otherwise the entry's
    /// note, which says what does answer it — Release for a path kept back, the connector's
    /// editor for a folder this sheet cannot reach.
    public func useDirectoryToken(_ kept: KeptPath) -> String? {
        guard kept.kind == .folder else { return note(for: kept) }
        let token = Placeholder.directoryToken
        if let name = PublishModel.hintName(kept.field, "env") {
            for index in envRows.indices where envRows[index].connector == kept.connector && envRows[index].name == name {
                envRows[index].hint = KeptValue.replacing(kept.value, in: envRows[index].hint, with: token)
            }
            return nil
        }
        if let name = PublishModel.hintName(kept.field, "needs") {
            for index in pathRows.indices where pathRows[index].connector == kept.connector
                && PublishModel.placeholderName(pathRows[index].name) == name {
                pathRows[index].hint = KeptValue.replacing(kept.value, in: pathRows[index].hint, with: token)
            }
            return nil
        }
        guard var entry = state.store.collections[collection]?.mcps[kept.connector],
              let config = CollectionDocument.usingDirectoryToken(in: entry.config, field: kept.field, folder: kept.value)
        else { return note(for: kept) }
        entry.config = config
        if let error = state.upsert(name: kept.connector, entry: entry, renamedFrom: kept.connector, in: collection) { return error }
        if collection == state.activeCollection { state.apply() }
        refreshRows(of: kept.connector)
        return nil
    }

    private func canWriteDirectoryToken(connector: String, field: String, folder: String) -> Bool {
        if let name = PublishModel.hintName(field, "env") {
            return envRows.contains { $0.connector == connector && $0.name == name }
        }
        if let name = PublishModel.hintName(field, "needs") {
            return pathRows.contains { $0.connector == connector && PublishModel.placeholderName($0.name) == name }
        }
        guard let config = state.store.collections[collection]?.mcps[connector]?.config else { return false }
        return CollectionDocument.usingDirectoryToken(in: config, field: field, folder: folder) != nil
    }

    /// The name in `env.NAME.hint` or `needs.NAME.hint`, the hints the sheet itself holds.
    private static func hintName(_ field: String, _ section: String) -> String? {
        let prefix = section + ".", suffix = ".hint"
        guard field.hasPrefix(prefix), field.hasSuffix(suffix), field.count > prefix.count + suffix.count else { return nil }
        return String(field.dropFirst(prefix.count).dropLast(suffix.count))
    }

    /// The rows of `connector` after its config changed under the sheet: an environment row shows
    /// the value it now holds, and an argument row that no longer reads as it did goes, since what
    /// it showed is not in the connector any more.
    private func refreshRows(of connector: String) {
        guard let config = state.store.collections[collection]?.mcps[connector]?.config else { return }
        let variables = PublishModel.env(of: config)
        envRows = envRows.map { row in
            guard row.connector == connector, let value = variables[row.name], value != row.value else { return row }
            return EnvRow(connector: row.connector, name: row.name, value: value, share: row.share, hint: row.hint)
        }
        let arguments = PublishModel.arguments(of: config)
        let now = Dictionary(uniqueKeysWithValues: arguments.enumerated().map { (JSONPointer(["args", String($0.offset)]), $0.element) })
        pathRows = pathRows.filter { $0.connector != connector || now[$0.pointer] == $0.value }
    }

    /// Lets one kept path travel as written in this collection's document, by the author's
    /// explicit choice after reading the preview. Everywhere: every row holding it is unticked,
    /// since a path both marked and released would be both a placeholder and not.
    ///
    /// A folder of this collection's own is never released, whatever the view offers: it is
    /// answered by `useDirectoryToken`, or by writing `${COLLECTION_DIR}` in the connector's
    /// editor. nil when the path is released; otherwise the note of the entry that still holds it
    /// back, and nothing changes.
    @discardableResult
    public func releaseKeptPath(_ value: String) -> String? {
        let text = KeptValue.nfc(value)
        if state.keptBack(for: collection).folders.contains(where: { KeptValue.nfc($0) == text }) {
            return keptPaths.first { KeptValue.nfc($0.value) == text }.map(note(for:))
        }
        released.insert(value)
        for index in pathRows.indices where pathRows[index].marked && KeptValue.nfc(pathRows[index].value) == text {
            pathRows[index].marked = false
        }
        return nil
    }

    /// Answers lost marks with the ticks made since `previous`: each row newly ticked, not ticked
    /// on open, answers the next unanswered lost mark of its connector in pointer order, and takes
    /// its name and hint while the author has not typed their own. A row unticked gives its lost
    /// mark back.
    private func answerLostMarks(since previous: [PathRow]) {
        let before = Dictionary(uniqueKeysWithValues: previous.map { ($0.id, $0.marked) })
        var rows = pathRows
        for row in rows where !row.marked { answers.removeValue(forKey: row.id) }
        var renamed = false
        for index in rows.indices {
            let row = rows[index]
            guard row.marked, before[row.id] == false, !tickedAtOpen.contains(row.id), answers[row.id] == nil else { continue }
            let taken = Set(answers.values)
            guard let mark = lostMarks.first(where: {
                $0.connector == row.connector && !forgotten.contains($0.id) && !taken.contains($0.id)
            }) else { continue }
            answers[row.id] = mark.id
            if row.name == namesAtOpen[row.id], row.hint.isEmpty {
                rows[index].name = mark.name
                rows[index].hint = mark.hint ?? ""
                renamed = true
            }
        }
        if renamed { pathRows = rows }
    }

    /// What the rows say, in the form the exporter reads. A marked row whose name is not a legal
    /// placeholder name is sanitized rather than dropped: the author ticked that row to keep a
    /// path on this machine out of the document, and silently publishing it because of how they
    /// spelled the name would be the one failure here nobody would notice.
    ///
    /// A lost mark is not in it: the preview shows what forgetting one would send, and nothing
    /// that records or writes this intent runs while one is unresolved.
    public var intent: PublishIntent {
        var shareValues: [String: Set<String>] = [:]
        var hints: [String: [String: String]] = [:]
        for row in envRows {
            if row.share { shareValues[row.connector, default: []].insert(row.name) }
            let hint = row.hint.trimmingCharacters(in: .whitespaces)
            if !hint.isEmpty { hints[row.connector, default: [:]][row.name] = hint }
        }
        var pathMarks: [String: [JSONPointer: PublishIntent.PathMark]] = [:]
        for row in pathRows where row.marked {
            let name = PublishModel.placeholderName(row.name)
            // Nothing to make a name out of is the one case left, and a marker with no name in
            // it is text nobody can fill: the row stays unmarked, which is visible in the
            // preview right under it.
            guard !name.isEmpty else { continue }
            let hint = row.hint.trimmingCharacters(in: .whitespaces)
            // The value is what lets the mark find its argument again once arguments move.
            pathMarks[row.connector, default: [:]][row.pointer] =
                PublishIntent.PathMark(name: name, hint: hint.isEmpty ? nil : hint, value: row.value)
        }
        return PublishIntent(shareValues: shareValues, pathMarks: pathMarks, hints: hints)
    }

    /// The document itself, as the editor would show it. Every byte that leaves this machine is
    /// in here, kept paths included, so the author reads them before releasing any
    /// (`keptPaths`). When the ticks can no longer be placed — the collection changed under the
    /// open sheet, or a ticked path is also copied unticked — there is no document, and the
    /// preview says why rather than showing one that would not be written.
    public var preview: String {
        do {
            return try state.exportDocument(for: collection, intent: intent, only: connectors).encode().editorText()
        } catch {
            return AppState.friendly(error)
        }
    }

    /// The text of every path the rows mark: what Publish or Export must not send as written
    /// anywhere else in the document, and what the sheet's Publish records as this machine's
    /// list of marked paths — the author's reviewed answer, replacing whatever was kept before.
    private var reviewedValues: Set<String> {
        Set(intent.pathMarks.values.flatMap { $0.values.compactMap(\.value) })
    }

    /// The first thing still waiting for the author, as the note the sheet shows for it.
    private var firstUnanswered: String? {
        if let lost = unresolvedMarks.first { return PublishModel.unresolvedMarkNote(lost.connector, lost.name) }
        if let kept = keptPaths.first { return note(for: kept) }
        return nil
    }

    /// The connectors a sheet over `collection` speaks for, which is every one of them unless an
    /// export ticked a subset.
    private static func held(in state: AppState, _ collection: String,
                             only: [String]?) -> [String: MCPEntry] {
        let all = state.store.collections[collection]?.mcps ?? [:]
        guard let only else { return all }
        let keep = Set(only)
        return all.filter { keep.contains($0.key) }
    }

    /// What the exporter cannot know is a secret: a value that looks like a credential and is
    /// about to travel. Never an edit — the author decides.
    public var warnings: [String] {
        let intent = self.intent
        let held = PublishModel.held(in: state, collection, only: connectors)
        return held.keys.sorted().flatMap { name -> [String] in
            guard let config = held[name]?.config else { return [] }
            return CollectionDocument.credentialWarnings(config, sharedEnv: intent.shareValues[name] ?? [])
                .map { PublishModel.warningLine(name, $0) }
        }
    }

    /// Publish, or re-publish with what the sheet now says. A folder that is not the one on
    /// record starts publishing again there, which is how the failed-write banner's Choose
    /// Folder… moves a collection. nil on success. Refused with the first note while a mark is
    /// unresolved or a kept path unanswered, behind the disabled button: what the rows say would
    /// send the path as written. What the author released goes on record with the ticks.
    public func publish() -> String? {
        if let note = firstUnanswered { return note }
        guard let chosen = folder?.trimmingCharacters(in: .whitespaces), !chosen.isEmpty else { return nil }
        let letGo = released.subtracting(reviewedValues)
        guard state.isPublished(collection), state.collectionsCache.published[collection]?.folder == chosen else {
            return state.startPublishing(collection, to: chosen, intent: intent, reviewedValues: reviewedValues,
                                         releasedValues: letGo)
        }
        // The same folder, already publishing: the ticks go on record, and then the document is
        // written whether or not it changed. Pressing Publish again is how a write that failed is
        // retried, and by then nothing about the document is different — only the folder is.
        _ = state.updatePublishIntent(collection, intent: intent, reviewedValues: reviewedValues, releasedValues: letGo)
        return state.republish(collection)
    }

    /// The same document, written once, binding nothing. nil on success. Refused with the first
    /// note while anything waits, as `publish()` is.
    public func export(to path: String) -> String? {
        if let note = firstUnanswered { return note }
        return state.writeExport(for: collection, intent: intent, to: path, only: connectors,
                                 reviewed: reviewedValues, released: released.subtracting(reviewedValues))
    }

    // MARK: - Rows

    /// The environment variables the exporter will read, from the same place it reads them: a
    /// remote connector's are its passthrough env, a local one's are the config's own.
    private static func env(of config: JSONValue) -> [String: String] {
        if let remote = RemotePattern.decode(config) { return remote.passthroughEnv }
        return FormMapper.analyze(config).model.env
    }

    /// Only a local connector's arguments are the author's own. A remote connector's are built by
    /// the launcher on each machine, so there is nothing there to mark.
    private static func arguments(of config: JSONValue) -> [String] {
        guard RemotePattern.decode(config) == nil else { return [] }
        return FormMapper.analyze(config).model.args
    }

    /// An argument worth offering as machine-specific: it is written as a path, or something by
    /// that name is on this disk. A flag or a URL is neither.
    private static func looksLikeAPath(_ argument: String) -> Bool {
        if Placeholder.containsMarker(argument) { return false }
        for prefix in ["/", "~", "./", "../"] where argument.hasPrefix(prefix) { return true }
        // A Windows path written on either platform: one letter, a colon, a backslash.
        let scalars = Array(argument.unicodeScalars)
        if scalars.count >= 3, CharacterSet.letters.contains(scalars[0]), scalars[1] == ":", scalars[2] == "\\" {
            return true
        }
        return FileManager.default.fileExists(atPath: argument)
    }

    /// A connector's recorded marks by the text each was made on, in NFC; where two share a text,
    /// the first in pointer order speaks for both.
    private static func marksByValue(_ marks: [JSONPointer: PublishIntent.PathMark]) -> [String: PublishIntent.PathMark] {
        var out: [String: PublishIntent.PathMark] = [:]
        for (_, mark) in sortedByPointer(marks) {
            if let value = mark.value.map(KeptValue.nfc), out[value] == nil { out[value] = mark }
        }
        return out
    }

    private static func sortedByPointer(_ marks: [JSONPointer: PublishIntent.PathMark]) -> [(key: JSONPointer, value: PublishIntent.PathMark)] {
        marks.sorted { $0.key.description.ordinallyPrecedes($1.key.description) }
    }

    /// "path", then "path_2", "path_3" — numbered inside each connector, since a recipient fills
    /// one connector's placeholders at a time.
    private static func defaultPathName(_ index: Int) -> String {
        index <= 1 ? "path" : "path_\(index)"
    }

    /// A legal placeholder name out of whatever the author typed: anything outside
    /// `[A-Za-z0-9_]` becomes "_" (a leading digit is legal in a marker name). Empty for a name
    /// that is only whitespace — there is nothing there to make a name out of.
    static func placeholderName(_ typed: String) -> String {
        let trimmed = typed.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return "" }
        var name = ""
        // Per Unicode scalar, as the Windows mirror walks runes: one character the name cannot
        // carry becomes one underscore on both platforms.
        for scalar in trimmed.unicodeScalars {
            name.unicodeScalars.append(Placeholder.isValidName(String(scalar)) ? scalar : "_")
        }
        return name
    }
}

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
    public static let exportButton = "Export…"
    public static let cancelButton = "Cancel"
    /// This sheet's own folder picker, not the failed-publish banner's button of the same words:
    /// a sheet's buttons are its model's, as Settings' and the Import sheet's already are.
    public static let chooseFolderButton = "Choose Folder…"
    /// What a screen reader says for the bare tick beside a path row, which has no visible label.
    public static let markPathLabel = "Mark as a path this machine supplies"

    public static func title(_ collection: String) -> String { "Publish “\(collection)”" }

    /// The sheet's own title in export mode. No trailing ellipsis: the one on the menu item that
    /// opens it (`PopoverModel.exportTitleFor`) says a sheet follows, and this is that sheet.
    public static func exportTitle(_ collection: String) -> String { "Export “\(collection)”" }

    /// "this Mac" is the platform-forced half of this sentence; the Windows mirror says "this PC".
    public static func folderLine(_ fileName: String) -> String { "writes \(fileName) from this Mac on every change" }

    public static func warningLine(_ connector: String, _ warning: String) -> String { "\(connector): \(warning)" }

    public static func footerLine(_ fileName: String, _ originShort: String) -> String { "\(fileName) · \(originShort)" }

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
    @Published public var pathRows: [PathRow]

    private let state: AppState

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
        let held = PublishModel.held(in: state, collection, only: connectors)
        for name in held.keys.sorted() {
            guard let config = held[name]?.config else { continue }
            let shared = intent.shareValues[name] ?? []
            let hints = intent.hints[name] ?? [:]
            let variables = PublishModel.env(of: config)
            for key in variables.keys.sorted() {
                env.append(EnvRow(connector: name, name: key, value: variables[key] ?? "",
                                  share: shared.contains(key), hint: hints[key] ?? ""))
            }
            var found = 0
            for (index, argument) in PublishModel.arguments(of: config).enumerated()
            where PublishModel.looksLikeAPath(argument) {
                found += 1
                let pointer = JSONPointer(["args", String(index)])
                let mark = intent.pathMarks[name]?[pointer]
                paths.append(PathRow(connector: name, pointer: pointer, value: argument,
                                     marked: mark != nil,
                                     name: mark?.name ?? PublishModel.defaultPathName(found),
                                     hint: mark?.hint ?? ""))
            }
        }
        envRows = env
        pathRows = paths
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

    public var canPublish: Bool { !(folder ?? "").isEmpty }

    /// What the rows say, in the form the exporter reads. A marked row whose name is not a legal
    /// placeholder name is sanitized rather than dropped: the author ticked that row to keep a
    /// path on this machine out of the document, and silently publishing it because of how they
    /// spelled the name would be the one failure here nobody would notice.
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
            pathMarks[row.connector, default: [:]][row.pointer] =
                PublishIntent.PathMark(name: name, hint: hint.isEmpty ? nil : hint)
        }
        return PublishIntent(shareValues: shareValues, pathMarks: pathMarks, hints: hints)
    }

    /// The document itself, as the editor would show it. Every byte that leaves this machine is
    /// in here.
    public var preview: String {
        state.exportDocument(for: collection, intent: intent, only: connectors).encode().editorText()
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
    /// Folder… moves a collection. nil on success.
    public func publish() -> String? {
        guard let chosen = folder?.trimmingCharacters(in: .whitespaces), !chosen.isEmpty else { return nil }
        guard state.isPublished(collection), state.collectionsCache.published[collection]?.folder == chosen else {
            return state.startPublishing(collection, to: chosen, intent: intent)
        }
        // The same folder, already publishing: the ticks go on record, and then the document is
        // written whether or not it changed. Pressing Publish again is how a write that failed is
        // retried, and by then nothing about the document is different — only the folder is.
        _ = state.updatePublishIntent(collection, intent: intent)
        return state.republish(collection)
    }

    /// The same document, written once, binding nothing. nil on success.
    public func export(to path: String) -> String? {
        state.writeExport(for: collection, intent: intent, to: path, only: connectors)
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

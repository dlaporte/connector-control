import Foundation
import Combine
import ConnectorControlCore

/// EditSheetView without the pixels: every field, switch rule,
/// validation string, and the save/remove flow. The two sheet-style
/// confirmations (loss warning, remove) are published state the view binds
/// to, with one method per button; the save-conflict alert goes through Dialogs.
@MainActor
public final class EditorModel: ObservableObject {
    public static let notValidJSON = "Not valid JSON — check for a stray brace, missing comma, or unquoted value."
    public static let jsonTip = "Tip: paste a README snippet or an mcpServers stanza — a wrapper or a bare \"name\": {…} entry is unwrapped automatically, and the name filled in."
    public static let urlHint = "Enter a valid http(s) URL, e.g. https://example.com/mcp"
    public static let remoteFooter = "Runs via npx mcp-remote — managed for you."
    public static let automaticCaption = "Uses the server's OAuth (a browser window opens on first use), or no auth if the server is open."
    public static let bearerCaption = "Sent as Authorization: Bearer …"
    /// mcp-remote reads --static-oauth-client-info literally (or from an @file); it has no
    /// env-var indirection for it the way the header flags do, so unlike the token and
    /// header fields this value ends up on the process command line.
    public static let oauthSecretCaption = "Passed to mcp-remote on its command line, which other programs running on this Mac can read."
    public static let invalidURLError = "Server URL must be a valid http(s) URL."
    public static let bearerTokenError = "Enter a bearer token."
    public static let headerNameError = "Enter a header name."
    public static let headerValueError = "Enter a header value."
    public static let clientIDError = "Enter a client ID."
    public static let commandError = "Command must not be empty."
    public static let envNamelessError = "An environment variable value is missing its name."
    public static let lossWarningPrefix = "Switching to Form view can’t fully represent this configuration. These elements would be lost or altered:\n"
    public static let switchAnywayButton = "Switch Anyway"
    public static let stayInJSONButton = "Stay in JSON"
    public static let saveAnywayButton = "Save Anyway"
    public static let removeButton = "Remove"
    public static let removeInformative = "A copy remains in Backups."
    public static let addArgumentTitle = "＋ Add argument"
    public static let addVariableTitle = "＋ Add variable"
    public static let changedOutsideDetail = "Saving will overwrite that change with this editor's version."
    public static let removedOutsideDetail = "Saving will add it back."
    public static let whatCanIChange = "What can I change?"
    public static let whatCanIChangeAnswer = "Fill in the highlighted values and switch it on or off. Everything else follows the source; make a local copy to change it."
    public static let needsValue = "needs your value"
    public static let needsPath = "needs your path"
    public static let makeLocalCopyButton = "Make Local Copy…"

    public static func lockedFieldsNote(_ collection: String) -> String { "Synced from \(collection) · read-only" }

    public static func publishedNote(_ folder: String) -> String {
        "Published to \(folder) — saving updates the file your team reads. Secrets stay here."
    }

    public static func importedNote(_ collection: String, _ date: String) -> String {
        "Imported from “\(collection)” on \(date). Edits stay here."
    }

    public static func propagateLabel(_ collections: String, _ connector: String) -> String {
        "Also apply this change to \(collections), which has an identical \(connector)"
    }

    public static func duplicateEnvError(_ name: String) -> String { "Duplicate environment variable name: \(name)" }

    public static func removeMessage(_ name: String) -> String { "Remove “\(name)”? \(removeInformative)" }

    public static func changedOutsideMessage(_ name: String) -> String { "“\(name)” changed outside this editor." }

    public static func removedOutsideMessage(_ name: String) -> String { "“\(name)” was removed outside this editor." }

    public static func additionalTitle(count: Int, keys: [String]) -> String { "\(count) field(s) not editable here: \(keys.joined(separator: ", ")) — switch to JSON to edit" }

    /// The grey line above the fields, and what it means for what the window may change.
    ///
    /// Mirror: windows/src/ConnectorControl.Core/State/EditorModel.cs
    public enum HeaderState: Equatable, Sendable {
        /// An ordinary connector in an ordinary local collection: today's editor, unchanged.
        case none
        /// Somebody else's collection: everything but the placeholders belongs to its author.
        case synced(collection: String)
        /// A local collection whose document this machine writes: saving rewrites it.
        case published(folder: String)
        /// A copy taken from another collection, which has gone its own way since.
        case imported(from: String, date: String)
    }

    public let target: EditTarget
    private let state: AppState
    private let dialogs: Dialogs
    private var subscription: AnyCancellable?
    private var collectionSubscription: AnyCancellable?
    private var suppressToolEvaluation = false
    /// True only for a brand-new connector still showing the remote
    /// template's placeholder command/args. Set at open; consumed by the
    /// first discard (below) or by adopting an edited JSON view into the
    /// form, since from then on the command/args are the user's own, not a
    /// re-derivable property of the current fields — a later Type toggle
    /// must not wipe what they typed. An unchanged JSON round trip (open
    /// JSON, switch straight back) leaves it set.
    private var isUntouchedTemplate: Bool
    /// What the connector was asking this machine for when the window opened. Fixed here rather
    /// than re-read, because this is what decides whether a field is locked: a field being typed
    /// into must not turn into a locked one between two keystrokes, which is what the live
    /// `isPlaceholder…` flags would do. Those stay, for the caution ring and the hint beneath.
    ///
    /// Keyed by row identity, not position, so inserting a row above a marker leaves the answer
    /// on the row that was asking.
    private var askedEnvRows: Set<UUID> = []
    private var askedArgs: Set<UUID> = []
    private var askedBearerToken = false
    private var askedHeaderValue = false
    private var askedClientSecret = false
    /// Whether the form was read-only when the snapshot above was taken. One transition retakes
    /// it: a collection that stops syncing turns every field into an ordinary editable one, and
    /// a field still rendered as the unmasked placeholder control would be a secret in the clear
    /// in a form that no longer locks anything.
    private var snapshotWasReadOnly = false
    /// Where each argument sat in the config the window opened on, by row identity. The publish
    /// record keys a path mark by its pointer, so a hint belongs to a position in the document
    /// that was published, not to whatever position the row holds now — and a published
    /// collection's editor is fully editable, so rows move. Fixed at open and never retaken,
    /// unlike the asked-for snapshot, on the assumption that the published document does not
    /// change under the window. A republish from the Collections window while this editor is
    /// open breaks that assumption: the hints then describe the document as it was at open.
    private var openArgIndexByRow: [UUID: Int] = [:]
    /// The other local collections that held a byte-identical copy of this connector when the
    /// window opened. Fixed there rather than re-derived: the checkbox names them, and the save
    /// that follows must write to the collections the user was shown, not to whatever matches
    /// by the time they click.
    public let propagateTargets: [String]

    // MARK: - Fields

    /// The propagate checkbox: off unless the user ticks it.
    @Published public var propagate = false

    @Published public private(set) var view: EditView {
        didSet { if oldValue != view { evaluateRequiredTool() } }
    }
    @Published public var name: String
    /// The Type picker (new targets only). Switching to Local discards the
    /// remote template's bridge invocation.
    @Published public var isRemote: Bool {
        didSet { isRemoteChanged(from: oldValue) }
    }
    @Published public var remoteURL: String
    @Published public var authKind: RemoteAuthKind = .automatic
    @Published public var bearerToken = ""
    @Published public var headerName = ""
    @Published public var headerValue = ""
    @Published public var oauthClientID = ""
    @Published public var oauthClientSecret = ""
    @Published public var oauthScopes = ""
    private var remoteExtraArgs: [String] = []
    private var remotePassthroughEnv: [String: String] = [:]
    private var remotePackage = RemotePattern.defaultPackage
    @Published public var command: String {
        didSet { if oldValue != command { evaluateRequiredTool() } }
    }
    @Published public var args: [ArgRow] {
        didSet { if oldValue != args { evaluateRequiredTool() } }
    }
    @Published public var envRows: [EnvRow]
    private var additional: [String: JSONValue]
    @Published public var jsonText: String {
        didSet {
            if oldValue != jsonText {
                recoveredJSON = PasteRecovery.recover(jsonText)
                validateJSON()
                evaluateRequiredTool()
            }
        }
    }
    /// `jsonText` recovered once per edit, in the didSet above; `validateJSON`
    /// and `computeRequiredTool` read this instead of recovering it again.
    private var recoveredJSON: PasteRecovery.Result?
    @Published public private(set) var jsonError: String?
    @Published public private(set) var validationError: String?
    /// Non-nil while the loss-warning sheet is up.
    @Published public private(set) var lossWarning: [String]?
    /// True while the remove confirmation sheet is up.
    @Published public private(set) var removeConfirmationPending = false
    /// The launcher this connector needs; nil for none, a path, or unparseable JSON.
    @Published public private(set) var requiredTool: Tool?

    public init(state: AppState, target: EditTarget, dialogs: Dialogs) {
        self.state = state
        self.target = target
        self.dialogs = dialogs
        isUntouchedTemplate = target.isNew && target.forcesRemote
        propagateTargets = EditorModel.twins(of: target, in: state)
        name = target.name
        view = target.entry.lastEditView
        let config = target.entry.config
        // Placeholders: every stored property needs a value before `load`
        // (an instance method) can run; it overwrites all of these.
        isRemote = false
        remoteURL = ""
        command = ""
        args = []
        envRows = []
        additional = [:]
        jsonText = config.editorText()
        recoveredJSON = PasteRecovery.recover(jsonText)
        load(config)
        takeAskedSnapshot(readOnly: isReadOnly)
        openArgIndexByRow = Dictionary(uniqueKeysWithValues: args.enumerated().map { ($0.element.id, $0.offset) })
        // On open, a cached status shows its note at once; an unknown one is
        // probed now. Later changes go through evaluateRequiredTool. The relay
        // makes the view re-read toolNote.
        subscription = state.$toolStatuses.dropFirst().sink { [weak self] _ in self?.objectWillChange.send() }
        // The sidecar is what says whether this collection is still synced, and Stop Syncing is
        // the one thing that unlocks a form under an open window. The retake comes before the
        // republish, so the record is already the new one by the time the view re-reads it.
        collectionSubscription = state.$collectionsFile.dropFirst().sink { [weak self] file in
            self?.retakeSnapshotIfUnlocked(with: file)
            self?.objectWillChange.send()
        }
        requiredTool = computeRequiredTool()
        if let initial = requiredTool, state.toolStatuses[initial] == nil {
            state.refreshTools([initial])
        }
    }

    public var windowTitle: String { target.windowTitle }

    public var showTypePicker: Bool { target.isNew }

    /// The segmented Picker's binding: a set is a request; a refused switch snaps back.
    public var viewSelection: EditView {
        get { view }
        set {
            requestView(newValue)
            objectWillChange.send()
        }
    }

    /// Basic URL syntax check for the remote form: http(s) scheme and a host.
    public var remoteURLValid: Bool { RemotePattern.isValidHTTPURL(remoteURL) }

    public var showURLHint: Bool { !remoteURL.isEmpty && !remoteURLValid }

    public var hasEnvRows: Bool { !envRows.isEmpty }

    public var hasAdditional: Bool { !additional.isEmpty }

    public var additionalTitle: String {
        EditorModel.additionalTitle(count: additional.count, keys: additional.keys.sorted())
    }

    public var additionalPreview: String { JSONValue.object(additional).editorText() }

    public var hasJSONError: Bool { jsonError != nil }


    public var lossWarningMessage: String {
        EditorModel.lossWarningPrefix + (lossWarning ?? []).joined(separator: "\n")
    }

    public var removeConfirmationMessage: String { EditorModel.removeMessage(target.name) }

    /// Save is disabled with a JSON error, or in the remote form without a valid URL. A
    /// read-only window saves only the placeholders, none of which can put it in either state,
    /// so its Save stays enabled.
    public var canSave: Bool {
        isReadOnly || !((view == .json && jsonError != nil) || (view == .form && isRemote && !remoteURLValid))
    }

    /// Remove leaves the footer's left slot to Make Local Copy… when the connector is not this
    /// machine's to delete.
    public var canRemove: Bool { !target.isNew && !isReadOnly }

    // MARK: - Collection

    /// The collection this window edits, resolved: a target that names none edits whatever is
    /// active, which is what every editor opened from the popover has always meant.
    public var collectionName: String { target.collection ?? state.activeCollection }

    /// A synced collection's connectors belong to the author of its document. Everything but
    /// the values the document asks this machine for is locked, and a save may move only those.
    public var isReadOnly: Bool { state.isSynced(collectionName) }

    /// EditorModel.cs calls this `Header`: C# forbids a property and a nested type of the same
    /// name on one class, and the type is the one both sides spell `HeaderState`.
    public var headerState: HeaderState {
        let collection = collectionName
        if state.isSynced(collection) { return .synced(collection: collection) }
        // Publishing is per machine: a collection somebody else publishes says nothing here,
        // because this machine writes no file for it.
        if let binding = state.collectionsCache.published[collection] {
            return .published(folder: binding.folder)
        }
        if let provenance = state.collectionsFile.collections[collection]?.provenance[target.name] {
            return .imported(from: provenance.from, date: provenance.date)
        }
        return .none
    }

    /// The one grey line the header shows, or nil for an ordinary local connector.
    public var headerNote: String? {
        switch headerState {
        case .none:
            return nil
        case .synced(let collection):
            return EditorModel.lockedFieldsNote(collection)
        case .published(let folder):
            return EditorModel.publishedNote(folder)
        case .imported(let from, let date):
            return EditorModel.importedNote(from, date)
        }
    }

    public var hasHeaderNote: Bool { headerNote != nil }

    /// The paste tip offers something a read-only JSON view cannot do.
    public var showJSONTip: Bool { !isReadOnly && jsonError == nil }

    public var showPropagate: Bool { !propagateTargets.isEmpty }

    public var propagateMessage: String {
        EditorModel.propagateLabel(propagateTargets.joined(separator: ", "), target.name)
    }

    /// The footer's Make Local Copy…, which takes Remove's slot for a synced connector. nil on
    /// success, else the message; the view closes the window on success.
    public func makeLocalCopy(into collection: String) -> String? {
        state.makeLocalCopy(of: [target.name], from: collectionName, into: collection)
    }

    private static func twins(of target: EditTarget, in state: AppState) -> [String] {
        let collection = target.collection ?? state.activeCollection
        // A connector that does not exist yet has no twins, and a synced collection's copy is
        // its author's — neither offers the checkbox.
        guard !target.isNew, state.kind(of: collection) == .local else { return [] }
        return state.localCollectionNames.filter { other in
            other != collection && state.store.collections[other]?.mcps[target.name]?.config == target.entry.config
        }
    }

    // MARK: - Placeholders

    /// What the last Apply recorded this connector asking this machine for, by marker name.
    private var collectionNeeds: [String: CollectionsFile.Need] {
        state.needs(of: target.name, in: collectionName)
    }

    /// The hint for the first marker still standing in `text`, if the document supplied one. A
    /// filled field carries no marker, so it asks for nothing and says nothing.
    private func hint(in text: String) -> String? {
        let needs = collectionNeeds
        for marker in Placeholder.names(in: text) {
            if let hint = needs[marker]?.hint { return hint }
        }
        return nil
    }

    /// EditorModel.cs takes the row itself: its EnvRow is an ObservableObject the view holds on
    /// to, while this one is a struct in an array, so the live value has to be looked up by id.
    public func isPlaceholder(envRow id: UUID) -> Bool {
        envRows.first { $0.id == id }.map { Placeholder.containsMarker($0.value) } ?? false
    }

    /// Takes an id rather than the row, for the reason `isPlaceholder(envRow:)` gives.
    public func placeholderHint(envRow id: UUID) -> String? {
        envRows.first { $0.id == id }.flatMap { hint(in: $0.value) }
    }

    /// Argument indexes still carrying a marker: locked in a synced collection, but live.
    public var argsWithPlaceholders: Set<Int> {
        Set(args.indices.filter { Placeholder.containsMarker(args[$0].value) })
    }

    /// EditorModel.cs calls this `PlaceholderHintForArg`: C# has no argument labels to tell it
    /// apart from the env-row overload.
    public func placeholderHint(arg index: Int) -> String? {
        args.indices.contains(index) ? hint(in: args[index].value) : nil
    }

    public var bearerTokenIsPlaceholder: Bool { Placeholder.containsMarker(bearerToken) }

    public var headerValueIsPlaceholder: Bool { Placeholder.containsMarker(headerValue) }

    public var clientSecretIsPlaceholder: Bool { Placeholder.containsMarker(oauthClientSecret) }

    /// What every field is asking for right now, recorded as the answer the locks will use.
    private func takeAskedSnapshot(readOnly: Bool) {
        askedEnvRows = Set(envRows.filter { Placeholder.containsMarker($0.value) }.map(\.id))
        askedArgs = Set(args.filter { Placeholder.containsMarker($0.value) }.map(\.id))
        askedBearerToken = Placeholder.containsMarker(bearerToken)
        askedHeaderValue = Placeholder.containsMarker(headerValue)
        askedClientSecret = Placeholder.containsMarker(oauthClientSecret)
        snapshotWasReadOnly = readOnly
    }

    /// Stop Syncing under an open window: the form stops locking anything, so the snapshot taken
    /// against a locked form has nothing left to protect and a field the user has since filled
    /// must go back to being an ordinary masked secret. Taken from the values as they stand, so
    /// a field still holding a marker keeps asking. The values are read as they stand now, not
    /// from the config the window opened on. The view gates its control choice on `isReadOnly`
    /// too, so this only decides which fields stay live inside a form that still locks the rest.
    ///
    /// The sidecar comes from the publisher rather than from AppState: `@Published` announces a
    /// change before the property holds it, so reading `isReadOnly` here would still be the
    /// answer this is trying to leave behind.
    private func retakeSnapshotIfUnlocked(with file: CollectionsFile) {
        guard snapshotWasReadOnly, file.kind(of: collectionName) != .synced else { return }
        takeAskedSnapshot(readOnly: false)
    }

    /// Whether this row was asking for a value when the window opened, which is what unlocks it
    /// in a read-only form. Stays true after the value is filled in, unlike `isPlaceholder`.
    public func asksFor(envRow id: UUID) -> Bool { askedEnvRows.contains(id) }

    /// The same question for an argument. Takes an index because that is what a view has, and
    /// resolves it through the row's identity so a row inserted above does not move the answer.
    public func asksFor(arg index: Int) -> Bool {
        args.indices.contains(index) && askedArgs.contains(args[index].id)
    }

    public var asksForBearerToken: Bool { askedBearerToken }

    public var asksForHeaderValue: Bool { askedHeaderValue }

    public var asksForClientSecret: Bool { askedClientSecret }

    // MARK: - Published hints

    /// The author's hints for the values publishing strips, by environment variable name. Empty
    /// unless this machine is the one publishing the collection: another machine's record says
    /// what it strips, not what this editor is looking at.
    private var publishedEnvHints: [String: String] {
        guard state.collectionsCache.published[collectionName] != nil else { return [:] }
        return state.collectionsFile.collections[collectionName]?.publish?.intent.hints[target.name] ?? [:]
    }

    /// The author's hint beside a value publishing strips. Distinct from `placeholderHint`, which
    /// reads the synced sidecar's needs and is therefore always nil in the published state.
    public func publishedHint(envRow id: UUID) -> String? {
        envRows.first { $0.id == id }.flatMap { publishedEnvHints[$0.name] }
    }

    /// An argument's hint, which the record keys by where the marker sits rather than by name.
    /// Resolved through the row's identity, as `asksFor(arg:)` is: a published collection's
    /// editor adds and removes arguments freely, and a hint read by today's position would sit
    /// beside whichever row happened to slide into it. nil for a row added since the window
    /// opened, which the published document has never described.
    public func publishedHint(arg index: Int) -> String? {
        guard state.collectionsCache.published[collectionName] != nil, args.indices.contains(index),
              let published = openArgIndexByRow[args[index].id] else { return nil }
        let marks = state.collectionsFile.collections[collectionName]?.publish?.intent.pathMarks[target.name] ?? [:]
        return marks[JSONPointer(["args", String(published)])]?.hint
    }

    /// Whether this connector has any author's hint to show at all, so the view can leave the
    /// column out rather than reserve space for nothing.
    public var hasPublishedHints: Bool {
        guard state.collectionsCache.published[collectionName] != nil else { return false }
        if !publishedEnvHints.isEmpty { return true }
        let marks = state.collectionsFile.collections[collectionName]?.publish?.intent.pathMarks[target.name] ?? [:]
        return marks.values.contains { $0.hint != nil }
    }

    // MARK: - Live placeholder flags

    public var bearerTokenHint: String? { hint(in: bearerToken) }

    public var headerValueHint: String? { hint(in: headerValue) }

    public var clientSecretHint: String? { hint(in: oauthClientSecret) }

    // MARK: - Tool note

    /// nil while the tool is unknown (not probed yet) or found. Never blocks Save.
    public var toolNote: ToolNote? {
        guard let tool = requiredTool else { return nil }
        return ToolNote.make(tool: tool, status: state.toolStatuses[tool])
    }

    private func computeRequiredTool() -> Tool? {
        if view == .json {
            return recoveredJSON.flatMap { ToolRequirement.requiredTool(for: $0.config) }
        }
        return isRemote ? .npx : ToolRequirement.requiredTool(command: command, args: args.map(\.value))
    }

    /// A change to a different tool re-probes it even if cached — the user may have just installed it.
    private func evaluateRequiredTool() {
        guard !suppressToolEvaluation else { return }
        let tool = computeRequiredTool()
        guard tool != requiredTool else { return }
        requiredTool = tool
        if let tool { state.refreshTools([tool]) }
    }

    /// Only a user's picker tap reaches the re-seed below: adoptForm assigns
    /// isRemote while `view` is still `.json`, so the guard skips it — the same
    /// outcome as the old view (its Type picker is out of the hierarchy while
    /// the JSON view shows) and as EditorModel.cs (which bypasses the setter).
    /// Both directions are tested.
    private func isRemoteChanged(from oldValue: Bool) {
        guard oldValue != isRemote else { return }
        if !isRemote, view == .form, isUntouchedTemplate {
            // Discard the remote template's bridge invocation — a local
            // server has nothing to do with mcp-remote. The template is
            // consumed by this one discard; from here on the fields are
            // the user's own local form, so a later switch back and forth
            // must not re-derive and repeat it.
            command = "npx"
            args = [ArgRow(value: "-y"), ArgRow(value: "")]
            isUntouchedTemplate = false
        }
        evaluateRequiredTool()
    }

    // MARK: - List editing

    public func addArg() { args.append(ArgRow(value: "")) }

    public func removeArg(id: UUID) { args.removeAll { $0.id == id } }

    /// A fresh row's value is shown in clear — the user is typing it, not
    /// inspecting a stored secret. Returns the row the view should focus.
    @discardableResult
    public func addEnvRow() -> UUID {
        let row = EnvRow(name: "", value: "", revealed: true)
        envRows.append(row)
        return row.id
    }

    public func removeEnvRow(id: UUID) { envRows.removeAll { $0.id == id } }

    public func toggleReveal(id: UUID) {
        guard let index = envRows.firstIndex(where: { $0.id == id }) else { return }
        envRows[index].revealed.toggle()
    }

    // MARK: - View switching

    public func requestView(_ requested: EditView) {
        guard requested != view else { return }
        if requested == .json {
            // The JSON view renders collapsedEnv(), which cannot represent duplicate
            // or nameless rows — switching would silently drop them, bypassing the
            // same validation Save enforces.
            if !isRemote, let envError = envValidationError() {
                validationError = envError
                return
            }
            validationError = nil
            jsonText = currentFormConfig().editorText()
            jsonError = nil
            view = .json
        } else {
            attemptSwitchToForm()
        }
    }

    private func attemptSwitchToForm() {
        guard let config = effectiveJSONConfig() else { return }
        let analysis = FormMapper.analyze(config)
        if analysis.isLossless {
            adoptForm(config)
            view = .form
            return
        }
        lossWarning = analysis.lost
    }

    /// The loss sheet's Stay in JSON button.
    public func stayInJSON() { lossWarning = nil }

    /// The loss sheet's Switch Anyway button.
    public func forceSwitchToForm() {
        lossWarning = nil
        guard let config = effectiveJSONConfig() else { return }
        adoptForm(config)
        view = .form
    }

    /// JSON → Form: `load` runs before `view` becomes `.form` (the caller
    /// flips it after this returns), which is what keeps `isRemoteChanged`'s
    /// bridge-discard branch — gated on `view == .form` — from firing mid-load.
    private func adoptForm(_ config: JSONValue) {
        let carried = carriedRecords()
        load(config)
        restore(carried)
        // A JSON edit that changed the config consumes the template, exactly
        // like a discard would — so a later Type toggle to Local re-derives
        // nothing and leaves what the user typed alone. An unchanged round
        // trip (config still equal to the template as opened) leaves the
        // flag set.
        isUntouchedTemplate = isUntouchedTemplate && config == target.entry.config
        evaluateRequiredTool()
    }

    /// Both row-keyed records, re-keyed onto something a rebuilt row still has. `load` gives every
    /// row a new identity, so without this a JSON round trip would leave the asked-for record and
    /// the published positions recognising no row at all — a synced form's placeholders would
    /// lock, a published form's argument hints would vanish. They are carried, never retaken: a
    /// retake reads current values and so would unlock nothing the user has already filled.
    ///
    /// Env rows are keyed by name, which is unique within a connector. Arguments are keyed by
    /// position, which is exact in a synced form — its JSON is read-only, so the round trip is
    /// always unchanged — and approximate only in an editable published form after a JSON edit
    /// that inserts, removes or reorders arguments, which is the one case this cannot follow.
    private struct CarriedRecords {
        let askedEnvNames: Set<String>
        let askedArgPositions: Set<Int>
        let openArgIndexByPosition: [Int: Int]
    }

    private func carriedRecords() -> CarriedRecords {
        CarriedRecords(
            askedEnvNames: Set(envRows.filter { askedEnvRows.contains($0.id) }.map(\.name)),
            askedArgPositions: Set(args.indices.filter { askedArgs.contains(args[$0].id) }),
            openArgIndexByPosition: Dictionary(uniqueKeysWithValues: args.indices.compactMap { position in
                openArgIndexByRow[args[position].id].map { (position, $0) }
            }))
    }

    private func restore(_ carried: CarriedRecords) {
        askedEnvRows = Set(envRows.filter { carried.askedEnvNames.contains($0.name) }.map(\.id))
        askedArgs = Set(carried.askedArgPositions.filter { args.indices.contains($0) }.map { args[$0].id })
        openArgIndexByRow = Dictionary(uniqueKeysWithValues: carried.openArgIndexByPosition
            .filter { args.indices.contains($0.key) }
            .map { (args[$0.key].id, $0.value) })
    }

    /// Loads `config` into every form/remote field and (re)computes `isRemote`.
    /// The one shared place `init` and `adoptForm` funnel through, so they
    /// cannot disagree on the isRemote rule or which fields a config fills in.
    /// Tool evaluation is suppressed for the duration — both callers evaluate
    /// once themselves, after `view` (for `computeRequiredTool`'s JSON branch)
    /// is in its final state.
    private func load(_ config: JSONValue) {
        suppressToolEvaluation = true
        let model = FormMapper.analyze(config).model
        command = model.command
        args = model.args.map { ArgRow(value: $0) }
        envRows = EditorModel.envRows(from: model.env)   // all values re-masked
        additional = model.additional
        let detected = RemotePattern.detect(config)
        isRemote = detected != nil || (target.forcesRemote && RemotePattern.isRemoteShaped(config))
        // Quirk kept intentionally: remoteURL comes ONLY from detect()'s
        // canonical 2-arg shape, even when isRemote is true via the
        // forcesRemote/isRemoteShaped fallback above — decode() may have found
        // a real URL past extra flags, but the Server URL field stays blank
        // until the user (re)types it.
        remoteURL = detected ?? ""
        if let remote = RemotePattern.decode(config) {
            applyRemoteFields(remote)
        } else {
            resetRemoteFields()
        }
        suppressToolEvaluation = false
    }

    private func resetRemoteFields() {
        authKind = .automatic
        bearerToken = ""
        headerName = ""
        headerValue = ""
        oauthClientID = ""
        oauthClientSecret = ""
        oauthScopes = ""
        remoteExtraArgs = []
        remotePassthroughEnv = [:]
        remotePackage = RemotePattern.defaultPackage
    }

    /// Maps a decoded RemoteConfig onto the form fields.
    private func applyRemoteFields(_ remote: RemoteConfig) {
        resetRemoteFields()
        switch remote.auth {
        case .automatic:
            break
        case .bearer(let token):
            authKind = .bearer
            bearerToken = token
        case .header(let headerName, let headerValue):
            authKind = .header
            self.headerName = headerName
            self.headerValue = headerValue
        case .oauthClient(let clientID, let clientSecret, let scopes):
            authKind = .oauthClient
            oauthClientID = clientID
            oauthClientSecret = clientSecret
            oauthScopes = scopes
        }
        remoteExtraArgs = remote.extraArgs
        remotePassthroughEnv = remote.passthroughEnv
        remotePackage = remote.package
    }

    private static func envRows(from env: [String: String]) -> [EnvRow] {
        env.sorted { $0.key < $1.key }.map { EnvRow(name: $0.key, value: $0.value) }
    }

    // MARK: - JSON

    private func validateJSON() {
        jsonError = recoveredJSON == nil ? EditorModel.notValidJSON : nil
    }

    /// Resolves the editor text via PasteRecovery, fills the name from a pasted
    /// stanza when blank, and rewrites the text to the canonical config.
    private func effectiveJSONConfig() -> JSONValue? {
        guard let recovered = PasteRecovery.recover(jsonText) else {
            jsonError = EditorModel.notValidJSON
            return nil
        }
        jsonError = nil
        if let pasted = recovered.name, name.trimmingCharacters(in: .whitespaces).isEmpty {
            name = pasted
        }
        jsonText = recovered.config.editorText()
        return recovered.config
    }

    // MARK: - Form → config

    private var currentRemoteAuth: RemoteAuth {
        switch authKind {
        case .automatic: return .automatic
        case .bearer: return .bearer(token: bearerToken)
        case .header: return .header(name: headerName, value: headerValue)
        case .oauthClient:
            return .oauthClient(clientID: oauthClientID, clientSecret: oauthClientSecret, scopes: oauthScopes)
        }
    }

    private func currentFormConfig() -> JSONValue {
        if isRemote {
            let encoded = RemotePattern.encode(RemoteConfig(
                url: remoteURL, auth: currentRemoteAuth,
                extraArgs: remoteExtraArgs, passthroughEnv: remotePassthroughEnv,
                package: remotePackage))
            // Preserve any unmodeled top-level keys (they can never collide with command/args/env).
            guard case .object(var object) = encoded, !additional.isEmpty else { return encoded }
            for (key, value) in additional { object[key] = value }
            return .object(object)
        }
        return FormMapper.serialize(FormModel(
            command: command, args: args.map(\.value), env: collapsedEnv(), additional: additional))
    }

    /// Names are kept VERBATIM; only rows with a blank name are left out; a
    /// later duplicate wins (validation blocks that before it can lose data).
    private func collapsedEnv() -> [String: String] {
        var env: [String: String] = [:]
        for row in envRows where !row.name.trimmingCharacters(in: .whitespaces).isEmpty {
            env[row.name] = row.value
        }
        return env
    }

    private func envValidationError() -> String? {
        var seen = Set<String>()
        for row in envRows {
            if row.name.trimmingCharacters(in: .whitespaces).isEmpty {
                if !row.value.isEmpty { return EditorModel.envNamelessError }
                continue   // a fully empty row (unused ＋ row) is just dropped
            }
            if !seen.insert(row.name).inserted {
                return EditorModel.duplicateEnvError(row.name)
            }
        }
        return nil
    }

    // MARK: - Save / remove

    /// The config this window opened on, with the `${CC_NEEDS:…}` leaves — and only those —
    /// carrying whatever the form now holds at the same JSON pointers. Anything else the fields
    /// have been talked into saying is dropped on the floor, which is the whole point: the
    /// author owns every other byte, and the next refresh would overwrite it anyway.
    private func placeholdersFilledIn() -> JSONValue {
        let original = target.entry.config
        let candidate = view == .json ? (PasteRecovery.recover(jsonText)?.config ?? original) : currentFormConfig()
        var result = original
        for (pointer, _) in Placeholder.markers(in: original) {
            guard case .string(let filled)? = candidate.value(at: pointer) else { continue }
            result = result.replacing(at: pointer, with: .string(filled)) ?? result
        }
        return result
    }

    /// True when the entry was saved and the window should close.
    public func save() -> Bool {
        validationError = nil
        let readOnly = isReadOnly
        let config: JSONValue
        if readOnly {
            // Nothing a read-only window can change can be invalid: the author's own save
            // validated everything else, and a placeholder takes any text at all.
            config = placeholdersFilledIn()
        } else if view == .json {
            guard let effective = effectiveJSONConfig() else { return false }
            config = effective
        } else {
            if isRemote {
                guard remoteURLValid else {
                    validationError = EditorModel.invalidURLError
                    return false
                }
                switch authKind {
                case .bearer where bearerToken.trimmingCharacters(in: .whitespaces).isEmpty:
                    validationError = EditorModel.bearerTokenError
                    return false
                case .header where headerName.trimmingCharacters(in: .whitespaces).isEmpty:
                    validationError = EditorModel.headerNameError
                    return false
                case .header where headerValue.isEmpty:
                    validationError = EditorModel.headerValueError
                    return false
                case .oauthClient where oauthClientID.trimmingCharacters(in: .whitespaces).isEmpty:
                    validationError = EditorModel.clientIDError
                    return false
                default:
                    break
                }
            } else if command.trimmingCharacters(in: .whitespaces).isEmpty {
                validationError = EditorModel.commandError
                return false
            }
            if !isRemote, let envError = envValidationError() {
                validationError = envError
                return false
            }
            config = currentFormConfig()
        }
        // Only the canonical `[-y] mcp-remote <url>` shape must carry a valid URL; extra-args invocations pass.
        if !readOnly, RemotePattern.isCanonicalShape(config), RemotePattern.detect(config) == nil {
            validationError = EditorModel.invalidURLError
            return false
        }
        // The editor works on a snapshot taken at window-open; if the store's copy has moved
        // underneath (external edit, delete, or rename reconciled in), do not silently
        // overwrite or resurrect it.
        let collection = collectionName
        var current: MCPEntry?
        if !target.isNew {
            current = state.store.collections[collection]?.mcps[target.name]
            if current?.config != target.entry.config {
                let missing = current == nil
                let message = missing
                    ? EditorModel.removedOutsideMessage(target.name)
                    : EditorModel.changedOutsideMessage(target.name)
                let detail = missing ? EditorModel.removedOutsideDetail : EditorModel.changedOutsideDetail
                guard dialogs.confirm(message: message, informative: detail, primary: EditorModel.saveAnywayButton) else {
                    return false
                }
            }
        }
        // The name and the remembered view are the author's too, so a read-only save leaves
        // both where it found them. The enabled flag is this machine's and is carried over
        // from the store, exactly as every other save does.
        let saved = readOnly ? target.name : name
        let entry = MCPEntry(enabled: current?.enabled ?? target.entry.enabled, config: config,
                             lastEditView: readOnly ? target.entry.lastEditView : view)
        if let error = state.upsert(name: saved, entry: entry, renamedFrom: target.isNew ? nil : target.name,
                                    in: target.collection) {
            validationError = error
            return false
        }
        if propagate { propagateSavedConfig(config, as: saved) }
        // Apply, then let the view dismiss on `true`. The old view dismissed first
        // and applied after (and EditorModel.cs raises CloseRequested before it
        // applies); performApply shows no UI, so the order is not observable.
        state.applyInteractively()
        return true
    }

    /// The ticked checkbox: the same change again in each collection that held an identical
    /// copy when this window opened. Each twin keeps its own on/off state, which is this
    /// machine's business and not part of "this change"; a name already taken in one of them
    /// leaves that collection alone rather than failing a save that has already landed.
    ///
    /// The checkbox promised these collections held an identical copy, and that was measured when
    /// the window opened. A twin that has moved since — a second editor window on it saved first,
    /// or an external edit reconciled in — is no longer the connector the user agreed to change,
    /// so it is skipped in silence. The same care the primary save takes over its own snapshot.
    private func propagateSavedConfig(_ config: JSONValue, as saved: String) {
        for other in propagateTargets {
            guard let twin = state.store.collections[other]?.mcps[target.name],
                  twin.config == target.entry.config else { continue }
            _ = state.upsert(name: saved,
                             entry: MCPEntry(enabled: twin.enabled, config: config, lastEditView: view),
                             renamedFrom: target.name, in: other)
        }
    }

    /// The Remove button: opens the confirmation sheet.
    public func requestRemove() { removeConfirmationPending = true }

    public func cancelRemove() { removeConfirmationPending = false }

    /// The sheet's Remove button: remove and apply in the same turn — a
    /// watcher-driven reload between the two once resurrected the connector.
    public func confirmRemove() {
        removeConfirmationPending = false
        state.remove(name: target.name, in: target.collection)
        state.applyInteractively()
    }

    /// Stops listening to AppState. The app does not call this: the
    /// subscription holds `self` weakly and dies with the `@StateObject`, and a
    /// re-shown view keeps its object. Tests call it to prove the republish is
    /// what repaints the view (EditorModel.cs disposes from the window's Closed).
    public func dispose() {
        subscription?.cancel()
        subscription = nil
        collectionSubscription?.cancel()
        collectionSubscription = nil
    }
}

import Foundation
import Combine
import ConnectorControlCore

/// Catalog §3 EditSheetView without the pixels: every field, switch rule,
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

    public static func duplicateEnvError(_ name: String) -> String { "Duplicate environment variable name: \(name)" }

    public static func removeMessage(_ name: String) -> String { "Remove “\(name)”? \(removeInformative)" }

    public static func changedOutsideMessage(_ name: String) -> String { "“\(name)” changed outside this editor." }

    public static func removedOutsideMessage(_ name: String) -> String { "“\(name)” was removed outside this editor." }

    public static func additionalTitle(count: Int, keys: [String]) -> String { "\(count) field(s) not editable here: \(keys.joined(separator: ", ")) — switch to JSON to edit" }

    public let target: EditTarget
    private let state: AppState
    private let dialogs: Dialogs
    private var subscription: AnyCancellable?
    private var suppressToolEvaluation = false
    /// True only for a brand-new connector still showing the remote
    /// template's placeholder command/args — set once at open, never
    /// re-derived from the current field values.
    private let isUntouchedTemplate: Bool

    // MARK: - Fields (catalog §3.3)

    @Published public private(set) var view: EditView {
        didSet { if oldValue != view { evaluateRequiredTool() } }
    }
    @Published public var name: String
    /// The Type picker (new targets only). Switching to Local discards the
    /// remote template's bridge invocation (catalog §3.6).
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
    /// Non-nil while the loss-warning sheet is up (catalog §3.5).
    @Published public private(set) var lossWarning: [String]?
    /// True while the remove confirmation sheet is up (catalog §3.10).
    @Published public private(set) var removeConfirmationPending = false
    /// The launcher this connector needs (spec 2026-09-05-tool-probe §3.3);
    /// nil for none, a path, or unparseable JSON.
    @Published public private(set) var requiredTool: Tool?

    public init(state: AppState, target: EditTarget, dialogs: Dialogs) {
        self.state = state
        self.target = target
        self.dialogs = dialogs
        isUntouchedTemplate = target.isNew && target.forcesRemote
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
        // Spec 2026-09-05-tool-probe §3.4: on open, a cached status shows its
        // note at once; an unknown one is probed now. Later changes go through
        // evaluateRequiredTool. The relay makes the view re-read toolNote.
        subscription = state.$toolStatuses.dropFirst().sink { [weak self] _ in self?.objectWillChange.send() }
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

    public var jsonStatusText: String { jsonError ?? EditorModel.jsonTip }

    public var lossWarningMessage: String {
        EditorModel.lossWarningPrefix + (lossWarning ?? []).joined(separator: "\n")
    }

    public var removeConfirmationMessage: String { EditorModel.removeMessage(target.name) }

    /// Catalog §3.4: Save is disabled with a JSON error, or in the remote form without a valid URL.
    public var canSave: Bool {
        !((view == .json && jsonError != nil) || (view == .form && isRemote && !remoteURLValid))
    }

    public var canRemove: Bool { !target.isNew }

    // MARK: - Tool note (spec 2026-09-05-tool-probe §3.3–§3.4)

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
    /// outcome as the old view (its Type picker was out of the hierarchy while
    /// the JSON view showed) and as EditorModel.cs (which bypasses the setter).
    /// Quality review Q54 asked; both directions are tested.
    private func isRemoteChanged(from oldValue: Bool) {
        guard oldValue != isRemote else { return }
        if !isRemote, view == .form, isUntouchedTemplate {
            // Discard the remote template's bridge invocation — a local
            // server has nothing to do with mcp-remote.
            command = "npx"
            args = [ArgRow(value: "-y"), ArgRow(value: "")]
        }
        evaluateRequiredTool()
    }

    // MARK: - List editing (catalog §3.6)

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

    // MARK: - View switching (catalog §3.5)

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
        load(config)
        evaluateRequiredTool()
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

    /// Maps a decoded RemoteConfig onto the form fields (catalog §3.3 authFields).
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

    // MARK: - JSON (catalog §3.7)

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

    // MARK: - Form → config (catalog §3.5 currentFormConfig)

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

    // MARK: - Save / remove (catalog §3.8–§3.10)

    /// True when the entry was saved and the window should close.
    public func save() -> Bool {
        validationError = nil
        let config: JSONValue
        if view == .json {
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
        if RemotePattern.isCanonicalShape(config), RemotePattern.detect(config) == nil {
            validationError = EditorModel.invalidURLError
            return false
        }
        // The editor works on a snapshot taken at window-open; if the store's copy moved
        // underneath (external edit, delete, or rename reconciled in), do not silently
        // overwrite or resurrect it.
        var current: MCPEntry?
        if !target.isNew {
            current = state.store.mcps[target.name]
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
        let entry = MCPEntry(enabled: current?.enabled ?? target.entry.enabled, config: config, lastEditView: view)
        if let error = state.upsert(name: name, entry: entry, renamedFrom: target.isNew ? nil : target.name) {
            validationError = error
            return false
        }
        // Apply, then let the view dismiss on `true`. The old view dismissed first
        // and applied after (and EditorModel.cs raises CloseRequested before it
        // applies); performApply shows no UI, so the order is not observable.
        state.applyInteractively()
        return true
    }

    /// The Remove button: opens the confirmation sheet.
    public func requestRemove() { removeConfirmationPending = true }

    public func cancelRemove() { removeConfirmationPending = false }

    /// The sheet's Remove button: remove and apply in the same turn — a
    /// watcher-driven reload between the two once resurrected the connector.
    public func confirmRemove() {
        removeConfirmationPending = false
        state.remove(name: target.name)
        state.applyInteractively()
    }

    /// Stops listening to AppState. The app does not call this: the
    /// subscription holds `self` weakly and dies with the `@StateObject`, and a
    /// re-shown view keeps its object. Tests call it to prove the republish is
    /// what repaints the view (EditorModel.cs disposes from the window's Closed).
    public func dispose() {
        subscription?.cancel()
        subscription = nil
    }
}

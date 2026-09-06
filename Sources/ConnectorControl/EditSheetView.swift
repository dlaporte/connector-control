import SwiftUI
import ConnectorControlCore

struct EditTarget: Identifiable, Codable, Hashable {
    let id: String          // UUID for new, name for existing
    var name: String
    var entry: MCPEntry
    var isNew: Bool
    var forcesRemote: Bool = false

    static func existing(name: String, entry: MCPEntry) -> EditTarget {
        EditTarget(id: name, name: name, entry: entry, isNew: false)
    }

    static func new(template: JSONValue) -> EditTarget {
        EditTarget(id: UUID().uuidString, name: "",
                   entry: MCPEntry(config: template), isNew: true)
    }

    /// Add-Remote flow: template has an empty URL that detect() can't classify,
    /// so the remote form is forced explicitly.
    static func newRemote() -> EditTarget {
        EditTarget(id: UUID().uuidString, name: "",
                   entry: MCPEntry(config: RemotePattern.make(url: "")),
                   isNew: true, forcesRemote: true)
    }
}

/// The four ways the Remote form can authenticate an `npx mcp-remote` invocation.
enum RemoteAuthKind: String, CaseIterable {
    case automatic, bearer, header, oauthClient

    var title: String {
        switch self {
        case .automatic: return "Automatic (OAuth / none)"
        case .bearer: return "Bearer token"
        case .header: return "Custom header"
        case .oauthClient: return "OAuth client ID/secret"
        }
    }
}

struct EditSheetView: View {
    @EnvironmentObject var state: AppState
    @Environment(\.dismiss) private var dismiss
    @Environment(\.dismissWindow) private var dismissWindow
    let target: EditTarget

    @State private var view: EditView
    @State private var name: String
    @State private var remoteURL: String        // non-nil pattern → remote form
    @State private var isRemote: Bool
    @State private var form: FormModel
    @State private var jsonText: String
    @State private var jsonError: String?
    @State private var lossWarning: [String]?   // non-nil → confirmation shown
    @State private var validationError: String?
    @State private var confirmRemove = false
    @State private var hostWindow: NSWindow?
    @State private var envRows: [EnvRow]
    @State private var envRevealed: Set<UUID> = []
    @FocusState private var envFocus: UUID?

    @State private var authKind: RemoteAuthKind = .automatic
    @State private var bearerToken = ""
    @State private var headerName = ""
    @State private var headerValue = ""
    @State private var oauthClientID = ""
    @State private var oauthClientSecret = ""
    @State private var oauthScopes = ""
    @State private var remoteExtraArgs: [String] = []
    @State private var remotePassthroughEnv: [String: String] = [:]

    init(target: EditTarget) {
        self.target = target
        _name = State(initialValue: target.name)
        _view = State(initialValue: target.entry.lastEditView)
        let detected = RemotePattern.detect(target.entry.config)
        _isRemote = State(initialValue: target.forcesRemote || detected != nil)
        _remoteURL = State(initialValue: detected ?? "")
        let model = FormMapper.analyze(target.entry.config).model
        _form = State(initialValue: model)
        _envRows = State(initialValue: EditSheetView.envRows(from: model.env))
        _jsonText = State(initialValue: target.entry.config.editorText())

        if let remote = RemotePattern.decode(target.entry.config) {
            let fields = EditSheetView.authFields(remote.auth)
            _authKind = State(initialValue: fields.kind)
            _bearerToken = State(initialValue: fields.bearerToken)
            _headerName = State(initialValue: fields.headerName)
            _headerValue = State(initialValue: fields.headerValue)
            _oauthClientID = State(initialValue: fields.oauthClientID)
            _oauthClientSecret = State(initialValue: fields.oauthClientSecret)
            _oauthScopes = State(initialValue: fields.oauthScopes)
            _remoteExtraArgs = State(initialValue: remote.extraArgs)
            _remotePassthroughEnv = State(initialValue: remote.passthroughEnv)
        }
    }

    /// Maps a decoded `RemoteAuth` to the form fields that represent it.
    private static func authFields(_ auth: RemoteAuth) -> (
        kind: RemoteAuthKind, bearerToken: String, headerName: String, headerValue: String,
        oauthClientID: String, oauthClientSecret: String, oauthScopes: String
    ) {
        switch auth {
        case .automatic:
            return (.automatic, "", "", "", "", "", "")
        case .bearer(let token):
            return (.bearer, token, "", "", "", "", "")
        case .header(let name, let value):
            return (.header, "", name, value, "", "", "")
        case .oauthClient(let clientID, let clientSecret, let scopes):
            return (.oauthClient, "", "", "", clientID, clientSecret, scopes)
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Spacer()
                Picker("View", selection: viewBinding) {
                    Text("Form").tag(EditView.form)
                    Text("JSON").tag(EditView.json)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                Spacer()
            }
            .padding(.vertical, 10)

            if view == .form { formBody } else { jsonBody }

            if let error = validationError {
                Text(error)
                    .font(.callout)
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.horizontal, 16)
                    .padding(.top, 6)
            }

            Divider()
            HStack {
                if !target.isNew {
                    Button("Remove", role: .destructive) { confirmRemove = true }
                }
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Save") { save() }
                    .keyboardShortcut(.defaultAction)
                    .disabled((view == .json && jsonError != nil)
                        || (view == .form && isRemote && !remoteURLValid))
            }
            .padding(16)
        }
        // Open large enough that the tallest standard form (a remote connector
        // with OAuth client fields) fits without an inner scroll bar; the user
        // may grow the window but not shrink it below this.
        .frame(minWidth: 540, idealWidth: 540, maxWidth: .infinity,
               minHeight: 620, idealHeight: 620, maxHeight: .infinity)
        .confirmationDialog(
            "Switching to Form view can’t fully represent this configuration. "
            + "These elements would be lost or altered:\n"
            + (lossWarning ?? []).joined(separator: "\n"),
            isPresented: Binding(get: { lossWarning != nil },
                                 set: { if !$0 { lossWarning = nil } }),
            titleVisibility: .visible
        ) {
            Button("Switch Anyway", role: .destructive) { forceSwitchToForm() }
            Button("Stay in JSON", role: .cancel) { lossWarning = nil }
        }
        .confirmationDialog(
            "Remove “\(target.name)”? A copy remains in Backups.",
            isPresented: $confirmRemove, titleVisibility: .visible
        ) {
            Button("Remove", role: .destructive) {
                // Remove and apply in the same runloop turn: a watcher-driven
                // reload between the two once resurrected the connector.
                state.remove(name: target.name)
                state.applyInteractively()
                // SwiftUI's dismissal actions have proven unreliable from a
                // dialog context in this window; close the AppKit window
                // directly once the dialog has torn down.
                DispatchQueue.main.async {
                    hostWindow?.close()
                }
            }
        }
        .background(WindowFinder { hostWindow = $0 })
        .onAppear {
            // A cached status shows its note at once; an unknown one is probed now.
            if let tool = requiredTool, state.toolStatuses[tool] == nil {
                state.refreshTools([tool])
            }
        }
        .onChange(of: requiredTool) { _, tool in
            // A different tool is probed again even if cached — the user may
            // have installed it since the last look.
            if let tool { state.refreshTools([tool]) }
        }
    }

    /// Basic URL syntax check for the remote form: http(s) scheme and a host.
    private var remoteURLValid: Bool {
        guard let url = URL(string: remoteURL),
              let scheme = url.scheme?.lowercased(),
              scheme == "http" || scheme == "https",
              url.host != nil else { return false }
        return true
    }

    /// The launcher this connector needs (spec §3.3): npx in the remote form,
    /// the Command field (through one `cmd /c`) in the local form, the parsed
    /// config in the JSON view; nil for none, a path, or unparseable JSON.
    private var requiredTool: Tool? {
        if view == .json {
            return PasteRecovery.recover(jsonText)
                .flatMap { ToolRequirement.requiredTool(for: $0.config) }
        }
        return isRemote ? .npx : ToolRequirement.requiredTool(command: form.command, args: form.args)
    }

    /// nil while the tool is unknown (not probed yet) or found where Claude looks.
    private var toolNote: ToolNote? {
        guard let tool = requiredTool else { return nil }
        return ToolNote.make(tool: tool, status: state.toolStatuses[tool])
    }

    // MARK: view switching

    private var viewBinding: Binding<EditView> {
        Binding(get: { view }, set: { requested in
            guard requested != view else { return }
            if requested == .json {
                // The JSON view renders collapsedEnv(), which can't represent
                // duplicate or nameless rows — switching would silently drop
                // them, bypassing the same validation Save enforces.
                if !isRemote, let envError = envValidationError() {
                    validationError = envError
                    return
                }
                validationError = nil
                syncFormIntoJSON()
                view = .json
            } else {
                attemptSwitchToForm()
            }
        })
    }

    private func attemptSwitchToForm() {
        guard let config = effectiveJSONConfig() else { return }
        let analysis = FormMapper.analyze(config)
        if analysis.isLossless {
            adoptForm(analysis.model, config: config)
            view = .form
        } else {
            lossWarning = analysis.lost
        }
    }

    private func forceSwitchToForm() {
        guard let config = effectiveJSONConfig() else { lossWarning = nil; return }
        adoptForm(FormMapper.analyze(config).model, config: config)
        lossWarning = nil
        view = .form
    }

    private func adoptForm(_ model: FormModel, config: JSONValue) {
        form = model
        envRows = EditSheetView.envRows(from: model.env)
        envRevealed = []
        let detected = RemotePattern.detect(config)
        isRemote = detected != nil
            || (target.forcesRemote && RemotePattern.isRemoteShaped(config))
        remoteURL = detected ?? ""

        if let remote = RemotePattern.decode(config) {
            let fields = EditSheetView.authFields(remote.auth)
            authKind = fields.kind
            bearerToken = fields.bearerToken
            headerName = fields.headerName
            headerValue = fields.headerValue
            oauthClientID = fields.oauthClientID
            oauthClientSecret = fields.oauthClientSecret
            oauthScopes = fields.oauthScopes
            remoteExtraArgs = remote.extraArgs
            remotePassthroughEnv = remote.passthroughEnv
        } else {
            authKind = .automatic
            bearerToken = ""
            headerName = ""
            headerValue = ""
            oauthClientID = ""
            oauthClientSecret = ""
            oauthScopes = ""
            remoteExtraArgs = []
            remotePassthroughEnv = [:]
        }
    }

    private static func envRows(from env: [String: String]) -> [EnvRow] {
        env.sorted { $0.key < $1.key }.map { EnvRow(name: $0.key, value: $0.value) }
    }

    /// The dictionary the current rows describe. Names are kept VERBATIM —
    /// loaded configs legitimately contain exotic keys (even ones differing
    /// only by whitespace), and trimming here once silently renamed keys the
    /// user never touched. Only rows with a blank name are left out; a later
    /// verbatim duplicate wins in the dict, but validation blocks both Save
    /// and the Form → JSON switch before that collapse can lose data.
    private func collapsedEnv() -> [String: String] {
        var env: [String: String] = [:]
        for row in envRows
        where !row.name.trimmingCharacters(in: .whitespaces).isEmpty {
            env[row.name] = row.value
        }
        return env
    }

    /// nil when the env rows are saveable; else a user-facing error. Shared
    /// by Save and the Form → JSON switch — both serialize `collapsedEnv()`,
    /// which silently drops what a dictionary can't represent.
    private func envValidationError() -> String? {
        var seen = Set<String>()
        for row in envRows {
            if row.name.trimmingCharacters(in: .whitespaces).isEmpty {
                if !row.value.isEmpty {
                    return "An environment variable value is missing its name."
                }
                continue  // a fully empty row (unused ＋ row) is just dropped
            }
            if !seen.insert(row.name).inserted {
                return "Duplicate environment variable name: \(row.name)"
            }
        }
        return nil
    }

    private func syncFormIntoJSON() {
        jsonText = currentFormConfig().editorText()
        jsonError = nil
    }

    /// Live validity check driving the inline error and Save's enabled state.
    /// Accepts anything PasteRecovery can interpret — a bare `"name": {…}`
    /// stanza copied out of an mcpServers block, a full wrapper, etc.
    @discardableResult
    private func validateJSON() -> Bool {
        if PasteRecovery.recover(jsonText) != nil { jsonError = nil; return true }
        jsonError = "Not valid JSON — check for a stray brace, missing comma, or unquoted value."
        return false
    }

    /// Resolves the editor text to a config via PasteRecovery, fills `name`
    /// from a pasted stanza when the field is blank, and rewrites `jsonText`
    /// to the canonical config so the user sees exactly what was accepted.
    private func effectiveJSONConfig() -> JSONValue? {
        guard let recovered = PasteRecovery.recover(jsonText) else {
            jsonError = "Not valid JSON — check for a stray brace, missing comma, or unquoted value."
            return nil
        }
        jsonError = nil
        if let n = recovered.name, name.trimmingCharacters(in: .whitespaces).isEmpty {
            name = n
        }
        jsonText = recovered.config.editorText()
        return recovered.config
    }

    /// Builds the `RemoteAuth` the auth fields currently describe, per `authKind`.
    private var currentRemoteAuth: RemoteAuth {
        switch authKind {
        case .automatic: return .automatic
        case .bearer: return .bearer(token: bearerToken)
        case .header: return .header(name: headerName, value: headerValue)
        case .oauthClient:
            return .oauthClient(clientID: oauthClientID, clientSecret: oauthClientSecret,
                                 scopes: oauthScopes)
        }
    }

    private func currentFormConfig() -> JSONValue {
        if isRemote {
            let encoded = RemotePattern.encode(RemoteConfig(
                url: remoteURL, auth: currentRemoteAuth,
                extraArgs: remoteExtraArgs, passthroughEnv: remotePassthroughEnv))
            // Preserve any unmodeled top-level keys (as the local path does):
            // FormMapper buckets non-command/args/env keys into `additional`.
            guard case .object(var obj) = encoded, !form.additional.isEmpty else {
                return encoded
            }
            for (key, value) in form.additional { obj[key] = value }
            return .object(obj)
        }
        return FormMapper.serialize(form)
    }

    // MARK: form body

    @ViewBuilder private var formBody: some View {
        Form {
            Section {
                if target.isNew {
                    Picker("Type", selection: $isRemote) {
                        Text("Remote").tag(true)
                        Text("Local").tag(false)
                    }
                    .pickerStyle(.segmented)
                    .fixedSize()
                    .onChange(of: isRemote) { _, nowRemote in
                        guard view == .form else { return }
                        if !nowRemote {
                            // Discard the remote template's bridge invocation — a
                            // local server has nothing to do with mcp-remote.
                            if form.args.contains("mcp-remote") || form.command.isEmpty {
                                form.command = "npx"
                                form.args = ["-y", ""]
                            }
                        }
                    }
                }
                TextField("Name", text: $name, prompt: Text("my-mcp"))
            }

            if isRemote {
                Section {
                    TextField("Server URL", text: $remoteURL,
                               prompt: Text("https://example.com/mcp"))
                    if !remoteURL.isEmpty && !remoteURLValid {
                        Text("Enter a valid http(s) URL, e.g. https://example.com/mcp")
                            .font(.caption)
                            .foregroundStyle(.red)
                    }
                    if let note = toolNote {
                        ToolNoteView(note: note)
                    }
                } footer: {
                    Text("Runs via npx mcp-remote — managed for you.")
                }
                Section("Authentication") { authEditor }
            } else {
                Section {
                    TextField("Command", text: $form.command, prompt: Text("npx"))
                    if let note = toolNote {
                        ToolNoteView(note: note)
                    }
                }
                Section("Arguments") { argsEditor }
                Section("Environment Variables") { envEditor }
            }

            if !form.additional.isEmpty {
                Section {
                    DisclosureGroup(
                        "\(form.additional.count) field(s) not editable here: "
                        + form.additional.keys.sorted().joined(separator: ", ")
                        + " — switch to JSON to edit"
                    ) {
                        Text(additionalPreview)
                            .font(.system(.caption, design: .monospaced))
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .font(.caption)
                }
            }
        }
        .formStyle(.grouped)
        .onChange(of: envRows) { form.env = collapsedEnv() }
    }

    private var additionalPreview: String {
        let data = (try? JSONValue.object(form.additional).serialized()) ?? Data()
        return String(decoding: data, as: UTF8.self)
    }

    @ViewBuilder private var argsEditor: some View {
        ForEach(form.args.indices, id: \.self) { index in
            HStack {
                TextField("argument", text: $form.args[index])
                    .font(.system(.body, design: .monospaced))
                Button { form.args.remove(at: index) } label: {
                    Image(systemName: "xmark.circle")
                }.buttonStyle(.plain)
            }
        }
        Button("＋ Add argument") { form.args.append("") }
            .buttonStyle(.plain).font(.caption).foregroundStyle(.secondary)
    }

    @ViewBuilder private var envEditor: some View {
        // A Grid (not per-row HStacks) so the Name/Value column headers stay
        // aligned with the fields beneath them; bordered fields make the click
        // targets visible inside the otherwise-borderless grouped form.
        if !envRows.isEmpty {
            Grid(alignment: .leading, horizontalSpacing: 8, verticalSpacing: 6) {
                GridRow {
                    Text("Name").font(.caption).foregroundStyle(.secondary)
                    Text("Value").font(.caption).foregroundStyle(.secondary)
                    Text("")
                }
                ForEach($envRows) { $row in
                    GridRow {
                        // Titles are kept for accessibility but hidden — the
                        // grouped form would render them as per-field labels,
                        // duplicating the column headers above.
                        TextField("Name", text: $row.name)
                            .labelsHidden()
                            .multilineTextAlignment(.leading)
                            .textFieldStyle(.roundedBorder)
                            .font(.system(.body, design: .monospaced))
                            .focused($envFocus, equals: row.id)
                        Group {
                            if envRevealed.contains(row.id) {
                                TextField("Value", text: $row.value)
                                    .font(.system(.body, design: .monospaced))
                            } else {
                                SecureField("Value", text: $row.value)
                            }
                        }
                        .labelsHidden()
                        .multilineTextAlignment(.leading)
                        .textFieldStyle(.roundedBorder)
                        HStack(spacing: 6) {
                            Button {
                                if envRevealed.contains(row.id) { envRevealed.remove(row.id) }
                                else { envRevealed.insert(row.id) }
                            } label: { Image(systemName: "eye") }.buttonStyle(.plain)
                            Button { envRows.removeAll { $0.id == row.id } } label: {
                                Image(systemName: "xmark.circle")
                            }.buttonStyle(.plain)
                        }
                    }
                }
            }
        }
        Button("＋ Add variable") {
            let row = EnvRow(name: "", value: "")
            // Reveal a fresh row's value — the user is typing it, not
            // inspecting a stored secret.
            envRevealed.insert(row.id)
            envRows.append(row)
            DispatchQueue.main.async { envFocus = row.id }
        }
        .buttonStyle(.plain).font(.caption).foregroundStyle(.secondary)
    }

    @ViewBuilder private var authEditor: some View {
        Picker("Type", selection: $authKind) {
            ForEach(RemoteAuthKind.allCases, id: \.self) { kind in
                Text(kind.title).tag(kind)
            }
        }
        .pickerStyle(.menu)

        switch authKind {
        case .automatic:
            Text("Uses the server's OAuth (a browser window opens on first use), "
                + "or no auth if the server is open.")
                .font(.caption)
                .foregroundStyle(.secondary)
        case .bearer:
            SecureField("Token", text: $bearerToken)
            Text("Sent as Authorization: Bearer …")
                .font(.caption)
                .foregroundStyle(.secondary)
        case .header:
            TextField("Header name", text: $headerName, prompt: Text("X-API-Key"))
            SecureField("Header value", text: $headerValue)
        case .oauthClient:
            TextField("Client ID", text: $oauthClientID)
            SecureField("Client Secret", text: $oauthClientSecret)
            TextField("Scopes (optional)", text: $oauthScopes, prompt: Text("space separated"))
        }
    }

    // MARK: json body

    @ViewBuilder private var jsonBody: some View {
        VStack(alignment: .leading, spacing: 8) {
            // Name lives in the Form's group box; JSON view needs its own so a
            // raw-JSON paste for a new MCP can be named without switching views.
            HStack(spacing: 8) {
                Text("Name")
                TextField("my-mcp", text: $name)
                    .textFieldStyle(.roundedBorder)
            }
            WrappingCodeEditor(text: $jsonText)
                .frame(maxHeight: .infinity)
                .overlay(RoundedRectangle(cornerRadius: 6)
                    .stroke(jsonError == nil ? Color.secondary.opacity(0.3) : .red))
                .onChange(of: jsonText) { validateJSON() }
            if let error = jsonError {
                Text(error).font(.caption).foregroundStyle(.red)
            } else {
                Text("Tip: paste a README snippet or an mcpServers stanza — a wrapper or a bare \"name\": {…} entry is unwrapped automatically, and the name filled in.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            if let note = toolNote {
                ToolNoteView(note: note)
            }
        }
        .padding(.horizontal, 16)
        .padding(.top, 8)
    }

    // MARK: save

    private func save() {
        validationError = nil
        var config: JSONValue
        if view == .json {
            guard let effective = effectiveJSONConfig() else { return }
            config = effective
        } else {
            if isRemote {
                guard let url = URL(string: remoteURL),
                      let scheme = url.scheme?.lowercased(),
                      scheme == "http" || scheme == "https", url.host != nil else {
                    validationError = "Server URL must be a valid http(s) URL."
                    return
                }
                switch authKind {
                case .automatic:
                    break
                case .bearer:
                    if bearerToken.trimmingCharacters(in: .whitespaces).isEmpty {
                        validationError = "Enter a bearer token."
                        return
                    }
                case .header:
                    if headerName.trimmingCharacters(in: .whitespaces).isEmpty {
                        validationError = "Enter a header name."
                        return
                    }
                    if headerValue.isEmpty {
                        validationError = "Enter a header value."
                        return
                    }
                case .oauthClient:
                    if oauthClientID.trimmingCharacters(in: .whitespaces).isEmpty {
                        validationError = "Enter a client ID."
                        return
                    }
                }
            } else if form.command.trimmingCharacters(in: .whitespaces).isEmpty {
                validationError = "Command must not be empty."
                return
            }
            if !isRemote, let envError = envValidationError() {
                validationError = envError
                return
            }
            config = currentFormConfig()
        }
        // Only the canonical `[-y] mcp-remote <url>` shape must carry a valid
        // URL; extra-args invocations (e.g. --header) are legitimate and pass.
        if RemotePattern.isCanonicalShape(config), RemotePattern.detect(config) == nil {
            validationError = "Server URL must be a valid http(s) URL."
            return
        }
        // The editor works on a snapshot taken at window-open; if the store's
        // copy moved underneath (external edit, delete, or rename reconciled
        // in), don't silently overwrite or resurrect it. A nil current config
        // (entry gone) also counts as a conflict.
        if !target.isNew,
           state.store.mcps[target.name]?.config != target.entry.config {
            let missing = state.store.mcps[target.name] == nil
            NSApp.activate(ignoringOtherApps: true)
            let alert = NSAlert()
            alert.messageText = missing
                ? "“\(target.name)” was removed outside this editor."
                : "“\(target.name)” changed outside this editor."
            alert.informativeText = missing
                ? "Saving will add it back."
                : "Saving will overwrite that change with this editor's version."
            alert.addButton(withTitle: "Save Anyway")
            alert.addButton(withTitle: "Cancel")
            guard alert.runModal() == .alertFirstButtonReturn else { return }
        }
        let entry = MCPEntry(
            enabled: state.store.mcps[target.name]?.enabled ?? target.entry.enabled,
            config: config,
            lastEditView: view)
        if let error = state.upsert(name: name, entry: entry,
                                    renamedFrom: target.isNew ? nil : target.name) {
            validationError = error
            return
        }
        dismiss()
        state.applyInteractively()
    }
}

/// A monospaced, word-wrapping plain-text editor. SwiftUI's TextEditor won't
/// char-wrap long unbroken tokens (URLs, secrets); this NSTextView does.
struct WrappingCodeEditor: NSViewRepresentable {
    @Binding var text: String

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSTextView.scrollableTextView()
        scroll.hasHorizontalScroller = false
        scroll.borderType = .noBorder
        guard let tv = scroll.documentView as? NSTextView else { return scroll }
        tv.isRichText = false
        tv.isAutomaticQuoteSubstitutionEnabled = false
        tv.isAutomaticDashSubstitutionEnabled = false
        tv.isAutomaticTextReplacementEnabled = false
        tv.font = .monospacedSystemFont(ofSize: NSFont.smallSystemFontSize, weight: .regular)
        tv.textContainerInset = NSSize(width: 4, height: 6)
        tv.isVerticallyResizable = true
        tv.isHorizontallyResizable = false
        tv.textContainer?.widthTracksTextView = true
        tv.textContainer?.lineBreakMode = .byCharWrapping
        tv.autoresizingMask = [.width]
        tv.delegate = context.coordinator
        tv.string = text
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let tv = scroll.documentView as? NSTextView, tv.string != text else { return }
        tv.string = text
    }

    final class Coordinator: NSObject, NSTextViewDelegate {
        let parent: WrappingCodeEditor
        init(_ p: WrappingCodeEditor) { parent = p }
        func textDidChange(_ n: Notification) {
            guard let tv = n.object as? NSTextView else { return }
            parent.text = tv.string
        }
    }
}

/// Hands the hosting NSWindow to SwiftUI state so it can be closed directly —
/// SwiftUI dismissal actions don't fire reliably from dialog contexts here.
private struct WindowFinder: NSViewRepresentable {
    var onFound: (NSWindow) -> Void

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        DispatchQueue.main.async { [weak view] in
            if let window = view?.window { onFound(window) }
        }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        DispatchQueue.main.async { [weak nsView] in
            if let window = nsView?.window { onFound(window) }
        }
    }
}

/// One editable environment-variable row. Rows carry a stable identity while
/// the name is edited — a dictionary key can't, since each keystroke would
/// re-key `form.env`, re-sort the ForEach, and drop field focus.
struct EnvRow: Identifiable, Equatable {
    let id = UUID()
    var name: String
    var value: String
}

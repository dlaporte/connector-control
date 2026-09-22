import SwiftUI
import ConnectorControlCore
import ConnectorControlState

/// Fields, bindings and layout only; every rule is EditorModel's.
struct EditSheetView: View {
    @StateObject private var model: EditorModel
    /// EditorModel republishes only on tool statuses, but its header, its locks and its
    /// placeholder hints all read AppState — so a collection that stops syncing while this
    /// window is open has to repaint it from here.
    @ObservedObject private var state: AppState
    @Environment(\.dismiss) private var dismiss
    @State private var hostWindow: NSWindow?
    @State private var whatCanIChangeShown = false
    /// What the collection's document asked this machine for when the window opened. The model's
    /// placeholder flags are deliberately live — a field stops asking the moment it is filled —
    /// which is what the caution mark and the hint want, and the opposite of what the field itself
    /// can stand: a value going in must not lock the box it is being typed into, or swap it for
    /// another control mid-keystroke. nil until the first appearance, when the live flags are
    /// still the open-time ones because nothing has been typed yet.
    @State private var asked: Asked?
    @FocusState private var envFocus: UUID?

    init(state: AppState, target: EditTarget) {
        self.state = state
        _model = StateObject(wrappedValue: EditorModel(state: state, target: target, dialogs: state.dialogs))
    }

    private struct Asked {
        var envRows: Set<UUID>
        var args: Set<Int>
        var bearerToken: Bool
        var headerValue: Bool
        var clientSecret: Bool
    }

    private func askedForEnv(_ id: UUID) -> Bool {
        asked.map { $0.envRows.contains(id) } ?? model.isPlaceholder(envRow: id)
    }

    private func askedForArg(_ index: Int) -> Bool {
        asked.map { $0.args.contains(index) } ?? model.argsWithPlaceholders.contains(index)
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Spacer()
                Picker("View", selection: $model.viewSelection) {
                    Text("Form").tag(EditView.form)
                    Text("JSON").tag(EditView.json)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                Spacer()
            }
            .padding(.vertical, 10)

            headerLine

            if model.view == .form { formBody } else { jsonBody }

            if let error = model.validationError {
                Text(error)
                    .font(.callout)
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.horizontal, 16)
                    .padding(.top, 6)
            }

            Divider()
            VStack(spacing: 10) {
                if model.showPropagate {
                    Toggle(model.propagateMessage, isOn: $model.propagate)
                        .toggleStyle(.checkbox)
                        .fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                HStack {
                    if model.canRemove {
                        Button(EditorModel.removeButton, role: .destructive) { model.requestRemove() }
                    } else if model.isReadOnly {
                        Menu(EditorModel.makeLocalCopyButton) {
                            ForEach(localCopyTargets, id: \.self) { name in
                                Button(name) { makeLocalCopy(into: name) }
                            }
                        }
                        .fixedSize()
                    }
                    Spacer()
                    Button(AlertDialogs.cancelTitle) { dismiss() }
                    Button("Save") { if model.save() { dismiss() } }
                        .keyboardShortcut(.defaultAction)
                        .disabled(!model.canSave)
                }
            }
            .padding(16)
        }
        // Open large enough that the tallest standard form (a remote connector
        // with OAuth client fields) fits without an inner scroll bar; the user
        // may grow the window but not shrink it below this.
        .frame(minWidth: 540, idealWidth: 540, maxWidth: .infinity,
               minHeight: 620, idealHeight: 620, maxHeight: .infinity)
        .confirmationDialog(
            model.lossWarningMessage,
            isPresented: Binding(get: { model.lossWarning != nil },
                                 set: { if !$0 { model.stayInJSON() } }),
            titleVisibility: .visible
        ) {
            Button(EditorModel.switchAnywayButton, role: .destructive) { model.forceSwitchToForm() }
            Button(EditorModel.stayInJSONButton, role: .cancel) { model.stayInJSON() }
        }
        .confirmationDialog(
            model.removeConfirmationMessage,
            isPresented: Binding(get: { model.removeConfirmationPending },
                                 set: { if !$0 { model.cancelRemove() } }),
            titleVisibility: .visible
        ) {
            Button(EditorModel.removeButton, role: .destructive) {
                model.confirmRemove()
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
            guard asked == nil else { return }
            asked = Asked(
                envRows: Set(model.envRows.filter { model.isPlaceholder(envRow: $0.id) }.map(\.id)),
                args: model.argsWithPlaceholders,
                bearerToken: model.bearerTokenIsPlaceholder,
                headerValue: model.headerValueIsPlaceholder,
                clientSecret: model.clientSecretIsPlaceholder)
        }
    }

    // MARK: header

    /// Where this connector came from, in one line. An ordinary connector in an ordinary local
    /// collection has no note and gets no line at all, not an empty one.
    @ViewBuilder private var headerLine: some View {
        if let note = model.headerNote {
            switch model.headerState {
            case .synced:
                HeaderCapsule(symbol: "link") {
                    Text(note)
                    Spacer(minLength: 8)
                    Button(EditorModel.whatCanIChange) { whatCanIChangeShown = true }
                        .buttonStyle(.link)
                        .popover(isPresented: $whatCanIChangeShown, arrowEdge: .bottom) {
                            Text(EditorModel.whatCanIChangeAnswer)
                                .font(.callout)
                                .fixedSize(horizontal: false, vertical: true)
                                .frame(width: 260)
                                .padding(12)
                        }
                }
            case .published:
                HeaderCapsule(symbol: "square.and.arrow.up") {
                    Text(note)
                }
            case .imported:
                Text(note)
                    .font(.caption)
                    .italic()
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 16)
                    .padding(.bottom, 8)
            case .none:
                // Unreachable: .none is the one state with no note.
                EmptyView()
            }
        }
    }

    // MARK: form body

    @ViewBuilder private var formBody: some View {
        Form {
            Section {
                if model.showTypePicker {
                    Picker(selection: $model.isRemote) {
                        Text("Remote").tag(true)
                        Text("Local").tag(false)
                    } label: {
                        LockedLabel("Type", locked: model.isReadOnly)
                    }
                    .pickerStyle(.segmented)
                    .fixedSize()
                    .disabled(model.isReadOnly)
                }
                TextField(text: $model.name, prompt: Text("my-mcp")) {
                    LockedLabel("Name", locked: model.isReadOnly)
                }
                .disabled(model.isReadOnly)
            }

            if model.isRemote {
                Section {
                    TextField(text: $model.remoteURL, prompt: Text("https://example.com/mcp")) {
                        LockedLabel("Server URL", locked: model.isReadOnly)
                    }
                    .disabled(model.isReadOnly)
                    if model.showURLHint {
                        Text(EditorModel.urlHint)
                            .font(.caption)
                            .foregroundStyle(.red)
                    }
                    if let note = model.toolNote {
                        ToolNoteView(note: note)
                    }
                } footer: {
                    Text(EditorModel.remoteFooter)
                }
                Section("Authentication") { authEditor }
            } else {
                Section {
                    TextField(text: $model.command, prompt: Text("npx")) {
                        LockedLabel("Command", locked: model.isReadOnly)
                    }
                    .disabled(model.isReadOnly)
                    if let note = model.toolNote {
                        ToolNoteView(note: note)
                    }
                }
                Section {
                    argsEditor
                } header: {
                    LockedLabel("Arguments", locked: model.isReadOnly)
                }
                Section("Environment Variables") { envEditor }
            }

            if model.hasAdditional {
                Section {
                    DisclosureGroup(model.additionalTitle) {
                        Text(model.additionalPreview)
                            .font(.system(.caption, design: .monospaced))
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .font(.caption)
                }
            }
        }
        .formStyle(.grouped)
    }

    @ViewBuilder private var argsEditor: some View {
        ForEach($model.args) { $row in
            // The model indexes arguments by position; SwiftUI hands over the row.
            let index = model.args.firstIndex { $0.id == row.id } ?? -1
            HStack(alignment: .top) {
                PlaceholderField(marked: model.argsWithPlaceholders.contains(index),
                                 hint: model.placeholderHint(arg: index)) {
                    if askedForArg(index) {
                        TextField("argument", text: $row.value, prompt: Text(EditorModel.needsPath))
                            .font(.system(.body, design: .monospaced))
                    } else {
                        TextField("argument", text: $row.value)
                            .font(.system(.body, design: .monospaced))
                            .disabled(model.isReadOnly)
                    }
                }
                Button { model.removeArg(id: row.id) } label: {
                    Image(systemName: "xmark.circle")
                }
                .buttonStyle(.plain)
                .disabled(model.isReadOnly)
            }
        }
        Button(EditorModel.addArgumentTitle) { model.addArg() }
            .buttonStyle(.plain).font(.caption).foregroundStyle(.secondary)
            .disabled(model.isReadOnly)
    }

    @ViewBuilder private var envEditor: some View {
        // A Grid (not per-row HStacks) so the Name/Value column headers stay
        // aligned with the fields beneath them; bordered fields make the click
        // targets visible inside the otherwise-borderless grouped form.
        if model.hasEnvRows {
            Grid(alignment: .leading, horizontalSpacing: 8, verticalSpacing: 6) {
                GridRow {
                    Text("Name").font(.caption).foregroundStyle(.secondary)
                    Text("Value").font(.caption).foregroundStyle(.secondary)
                    Text("")
                }
                ForEach($model.envRows) { $row in
                    // A synced collection's save projection matches leaves by pointer, so the
                    // name and the row itself belong to the author even here: only a value the
                    // document asked this machine for is live.
                    GridRow(alignment: .top) {
                        // Titles are kept for accessibility but hidden — the
                        // grouped form would render them as per-field labels,
                        // duplicating the column headers above.
                        TextField("Name", text: $row.name)
                            .labelsHidden()
                            .multilineTextAlignment(.leading)
                            .textFieldStyle(.roundedBorder)
                            .font(.system(.body, design: .monospaced))
                            .focused($envFocus, equals: row.id)
                            .disabled(model.isReadOnly)
                        PlaceholderField(marked: model.isPlaceholder(envRow: row.id),
                                         hint: model.placeholderHint(envRow: row.id)) {
                            Group {
                                if askedForEnv(row.id) {
                                    // A marker is nobody's secret, and masking it would hide
                                    // which value the author is asking for.
                                    TextField("Value", text: $row.value, prompt: Text(EditorModel.needsValue))
                                } else if row.revealed {
                                    TextField("Value", text: $row.value)
                                        .disabled(model.isReadOnly)
                                } else {
                                    SecureField("Value", text: $row.value)
                                        .disabled(model.isReadOnly)
                                }
                            }
                            .labelsHidden()
                            .multilineTextAlignment(.leading)
                            .textFieldStyle(.roundedBorder)
                        }
                        HStack(spacing: 6) {
                            Button { model.toggleReveal(id: row.id) } label: {
                                Image(systemName: "eye")
                            }.buttonStyle(.plain)
                            Button { model.removeEnvRow(id: row.id) } label: {
                                Image(systemName: "xmark.circle")
                            }
                            .buttonStyle(.plain)
                            .disabled(model.isReadOnly)
                        }
                    }
                }
            }
        }
        Button(EditorModel.addVariableTitle) {
            let id = model.addEnvRow()
            DispatchQueue.main.async { envFocus = id }
        }
        .buttonStyle(.plain).font(.caption).foregroundStyle(.secondary)
        .disabled(model.isReadOnly)
    }

    @ViewBuilder private var authEditor: some View {
        Picker(selection: $model.authKind) {
            ForEach(RemoteAuthKind.allCases, id: \.self) { kind in
                Text(kind.title).tag(kind)
            }
        } label: {
            LockedLabel("Type", locked: model.isReadOnly)
        }
        .pickerStyle(.menu)
        .disabled(model.isReadOnly)

        switch model.authKind {
        case .automatic:
            Text(EditorModel.automaticCaption)
                .font(.caption)
                .foregroundStyle(.secondary)
        case .bearer:
            PlaceholderField(marked: model.bearerTokenIsPlaceholder, hint: model.bearerTokenHint) {
                if asked?.bearerToken ?? model.bearerTokenIsPlaceholder {
                    SecureField("Token", text: $model.bearerToken, prompt: Text(EditorModel.needsValue))
                } else {
                    SecureField("Token", text: $model.bearerToken)
                        .disabled(model.isReadOnly)
                }
            }
            Text(EditorModel.bearerCaption)
                .font(.caption)
                .foregroundStyle(.secondary)
        case .header:
            TextField("Header name", text: $model.headerName, prompt: Text("X-API-Key"))
                .disabled(model.isReadOnly)
            PlaceholderField(marked: model.headerValueIsPlaceholder, hint: model.headerValueHint) {
                if asked?.headerValue ?? model.headerValueIsPlaceholder {
                    SecureField("Header value", text: $model.headerValue, prompt: Text(EditorModel.needsValue))
                } else {
                    SecureField("Header value", text: $model.headerValue)
                        .disabled(model.isReadOnly)
                }
            }
        case .oauthClient:
            TextField("Client ID", text: $model.oauthClientID)
                .disabled(model.isReadOnly)
            PlaceholderField(marked: model.clientSecretIsPlaceholder, hint: model.clientSecretHint) {
                if asked?.clientSecret ?? model.clientSecretIsPlaceholder {
                    SecureField("Client Secret", text: $model.oauthClientSecret, prompt: Text(EditorModel.needsValue))
                } else {
                    SecureField("Client Secret", text: $model.oauthClientSecret)
                        .disabled(model.isReadOnly)
                }
            }
            Text(EditorModel.oauthSecretCaption)
                .font(.caption)
                .foregroundStyle(.secondary)
            TextField("Scopes (optional)", text: $model.oauthScopes, prompt: Text("space separated"))
                .disabled(model.isReadOnly)
        }
    }

    // MARK: json body

    @ViewBuilder private var jsonBody: some View {
        VStack(alignment: .leading, spacing: 8) {
            // Name lives in the Form's group box; JSON view needs its own so a
            // raw-JSON paste for a new MCP can be named without switching views.
            HStack(spacing: 8) {
                LockedLabel("Name", locked: model.isReadOnly)
                TextField("my-mcp", text: $model.name)
                    .textFieldStyle(.roundedBorder)
                    .disabled(model.isReadOnly)
            }
            if model.isReadOnly {
                // WrappingCodeEditor wraps an editable NSTextView, and SwiftUI's disabled state
                // does not reach inside an NSViewRepresentable — so the read-only view of the
                // same bytes is a selectable text pane rather than a greyed-out editor.
                ScrollView {
                    Text(model.jsonText)
                        .font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(6)
                }
                .frame(maxHeight: .infinity)
                .overlay(RoundedRectangle(cornerRadius: 6)
                    .stroke(Color.secondary.opacity(0.3)))
            } else {
                WrappingCodeEditor(text: $model.jsonText)
                    .frame(maxHeight: .infinity)
                    .overlay(RoundedRectangle(cornerRadius: 6)
                        .stroke(model.hasJSONError ? Color.red : Color.secondary.opacity(0.3)))
            }
            // The two halves of jsonStatusText, bound apart: the tip offers a paste this pane
            // cannot accept when the connector is somebody else's.
            if let error = model.jsonError {
                Text(error)
                    .font(.caption)
                    .foregroundStyle(.red)
            } else if model.showJSONTip {
                Text(EditorModel.jsonTip)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            if let note = model.toolNote {
                ToolNoteView(note: note)
            }
        }
        .padding(.horizontal, 16)
        .padding(.top, 8)
    }

    // MARK: make local copy

    /// The local collections this connector can be copied into. Its own is synced, so it never
    /// appears; the filter says so rather than relying on it.
    private var localCopyTargets: [String] {
        state.localCollectionNames.filter { $0 != model.collectionName }
    }

    private func makeLocalCopy(into collection: String) {
        // The one failure AppState reports here is a target that is not local, which a menu
        // built from the local collections cannot offer.
        if model.makeLocalCopy(into: collection) == nil { dismiss() }
    }
}

/// The grey line the synced and published headers share: a glyph, then whatever the state has
/// to say.
private struct HeaderCapsule<Content: View>: View {
    let symbol: String
    @ViewBuilder let content: Content

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: symbol)
            content
        }
        .font(.caption)
        .padding(.horizontal, 9)
        .padding(.vertical, 5)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.secondary.opacity(0.12), in: RoundedRectangle(cornerRadius: 7))
        .padding(.horizontal, 16)
        .padding(.bottom, 8)
    }
}

/// A field label with the small lock that marks a value belonging to the collection's author.
private struct LockedLabel: View {
    private let title: String
    private let locked: Bool

    init(_ title: String, locked: Bool) {
        self.title = title
        self.locked = locked
    }

    var body: some View {
        HStack(spacing: 4) {
            Text(title)
            if locked {
                Image(systemName: "lock.fill")
                    .font(.caption2)
                    .opacity(0.55)
            }
        }
    }
}

/// A value the collection's document asks this machine for: the caution colour round the field
/// and, under it, whatever the author said about finding it.
private struct PlaceholderField<Content: View>: View {
    let marked: Bool
    let hint: String?
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            if marked {
                content
                    .overlay(RoundedRectangle(cornerRadius: 5).stroke(Color.orange))
            } else {
                content
            }
            if marked, let hint {
                Text(hint)
                    .font(.caption)
                    .foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

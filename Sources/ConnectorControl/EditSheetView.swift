import SwiftUI
import ConnectorControlCore
import ConnectorControlState

/// Fields, bindings and layout only; every rule is EditorModel's.
struct EditSheetView: View {
    @StateObject private var model: EditorModel
    /// EditorModel republishes on tool statuses and on the sidecar, whose change is what unlocks
    /// the form and retakes its snapshot. Its header and hints also read the cache and the store,
    /// which it does not relay, so this window observes AppState itself for those. The Windows
    /// model raises on all three instead, since a WPF window has no whole-object republish.
    @ObservedObject private var state: AppState
    @Environment(\.dismiss) private var dismiss
    @State private var whatCanIChangeShown = false
    @FocusState private var envFocus: UUID?

    init(state: AppState, target: EditTarget) {
        self.state = state
        _model = StateObject(wrappedValue: EditorModel(state: state, target: target, dialogs: state.dialogs))
    }

    /// What the lock glyph says to a screen reader. A glyph on its own reads as nothing, and a
    /// lock is only ever on screen while the collection is synced, so this is the very sentence
    /// the header shows — which is what the Windows glyph names too.
    private var lockLabel: String { EditorModel.lockedFieldsNote(model.collectionName) }

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
                FailureLine(error)
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
                    Spacer()
                    // Escape cancels, as it does in the Windows editor: this is a window rather
                    // than a sheet, and a window has no cancel key until a button claims it.
                    Button(AlertDialogs.cancelTitle) { dismiss() }
                        .keyboardShortcut(.cancelAction)
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
                        LockedLabel("Type", locked: model.isReadOnly, lockLabel: lockLabel)
                    }
                    .pickerStyle(.segmented)
                    .fixedSize()
                    .disabled(model.isReadOnly)
                }
                TextField(text: $model.name, prompt: Text("my-mcp")) {
                    LockedLabel("Name", locked: model.isReadOnly, lockLabel: lockLabel)
                }
                .disabled(model.isReadOnly)
            }

            if model.isRemote {
                Section {
                    TextField(text: $model.remoteURL, prompt: Text("https://example.com/mcp")) {
                        LockedLabel("Server URL", locked: model.isReadOnly, lockLabel: lockLabel)
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
                        LockedLabel("Command", locked: model.isReadOnly, lockLabel: lockLabel)
                    }
                    .disabled(model.isReadOnly)
                    if let note = model.toolNote {
                        ToolNoteView(note: note)
                    }
                }
                Section("Arguments") { argsEditor }
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
            let asks = model.asksFor(arg: index)
            HStack(alignment: .top) {
                PlaceholderField(marked: model.isOwed(arg: index),
                                 needs: EditorModel.needsPath,
                                 hint: model.placeholderHint(arg: index),
                                 published: model.publishedHint(arg: index)) {
                    TextField("argument", text: $row.value,
                              prompt: asks ? Text(EditorModel.needsPath) : nil)
                        .font(.system(.body, design: .monospaced))
                        .disabled(model.isReadOnly && !asks)
                }
                Button { model.removeArg(id: row.id) } label: {
                    Image(systemName: "xmark.circle")
                }
                .buttonStyle(.plain)
                .disabled(model.isReadOnly)
                .help(EditorModel.removeArgumentLabel)
                .accessibilityLabel(EditorModel.removeArgumentLabel)
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
                        PlaceholderField(marked: model.isOwed(envRow: row.id),
                                         needs: EditorModel.needsValue,
                                         hint: model.placeholderHint(envRow: row.id),
                                         published: model.publishedHint(envRow: row.id)) {
                            Group {
                                if model.asksFor(envRow: row.id) {
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
                            let reveal = row.revealed ? EditorModel.hideValueLabel : EditorModel.showValueLabel
                            Button { model.toggleReveal(id: row.id) } label: {
                                Image(systemName: "eye")
                            }
                            .buttonStyle(.plain)
                            .help(reveal)
                            .accessibilityLabel(reveal)
                            Button { model.removeEnvRow(id: row.id) } label: {
                                Image(systemName: "xmark.circle")
                            }
                            .buttonStyle(.plain)
                            .disabled(model.isReadOnly)
                            .help(EditorModel.removeVariableLabel)
                            .accessibilityLabel(EditorModel.removeVariableLabel)
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
            LockedLabel("Type", locked: model.isReadOnly, lockLabel: lockLabel)
        }
        .pickerStyle(.menu)
        .disabled(model.isReadOnly)

        switch model.authKind {
        case .automatic:
            Text(EditorModel.automaticCaption)
                .font(.caption)
                .foregroundStyle(.secondary)
        case .bearer:
            let asksToken = model.asksForBearerToken
            PlaceholderField(marked: model.bearerTokenOwed,
                             needs: EditorModel.needsValue,
                             hint: model.bearerTokenHint,
                             published: nil) {
                SecureField("Token", text: $model.bearerToken,
                            prompt: asksToken ? Text(EditorModel.needsValue) : nil)
                    .disabled(model.isReadOnly && !asksToken)
            }
            Text(EditorModel.bearerCaption)
                .font(.caption)
                .foregroundStyle(.secondary)
        case .header:
            TextField("Header name", text: $model.headerName, prompt: Text("X-API-Key"))
                .disabled(model.isReadOnly)
            let asksHeaderValue = model.asksForHeaderValue
            PlaceholderField(marked: model.headerValueOwed,
                             needs: EditorModel.needsValue,
                             hint: model.headerValueHint,
                             published: nil) {
                SecureField("Header value", text: $model.headerValue,
                            prompt: asksHeaderValue ? Text(EditorModel.needsValue) : nil)
                    .disabled(model.isReadOnly && !asksHeaderValue)
            }
        case .oauthClient:
            TextField("Client ID", text: $model.oauthClientID)
                .disabled(model.isReadOnly)
            let asksSecret = model.asksForClientSecret
            PlaceholderField(marked: model.clientSecretOwed,
                             needs: EditorModel.needsValue,
                             hint: model.clientSecretHint,
                             published: nil) {
                SecureField("Client Secret", text: $model.oauthClientSecret,
                            prompt: asksSecret ? Text(EditorModel.needsValue) : nil)
                    .disabled(model.isReadOnly && !asksSecret)
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
                LockedLabel("Name", locked: model.isReadOnly, lockLabel: lockLabel)
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
            // The error and the paste tip are separate lines: the tip offers a paste this pane
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
    private let lockLabel: String

    init(_ title: String, locked: Bool, lockLabel: String) {
        self.title = title
        self.locked = locked
        self.lockLabel = lockLabel
    }

    var body: some View {
        HStack(spacing: 4) {
            Text(title)
            if locked {
                Image(systemName: "lock.fill")
                    .font(.caption2)
                    .opacity(LockMark.opacity)
                    .accessibilityLabel(lockLabel)
            }
        }
    }
}

/// A value the collection's document asks this machine for: the caution colour round the field,
/// then the phrase that names the state and whatever the author said about finding it.
private struct PlaceholderField<Content: View>: View {
    let marked: Bool
    let needs: String
    let hint: String?
    /// What a published connector tells its readers about the value it strips out of the
    /// document. Nothing to do with the caution state: this machine keeps the secret itself.
    let published: String?
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            // One overlay whatever the state, so that filling the value recolours the ring rather
            // than replacing the field the value is being typed into.
            content
                .overlay(RoundedRectangle(cornerRadius: 5)
                    .stroke(marked ? Color.orange : Color.clear))
                // The caution line beneath is sight-only; the field itself carries the author's
                // hint for anyone reading the form aloud. An empty hint is no hint.
                .accessibilityHint(Text(hint ?? ""))
            if marked {
                // The field itself holds the marker the document left, so the phrase that names
                // the state goes here rather than in a prompt nothing empty would show.
                Text(needs)
                    .font(.caption)
                    .foregroundStyle(.orange)
                if let hint {
                    Text(hint)
                        .font(.caption)
                        .foregroundStyle(.orange)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            if let published {
                Text(published)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

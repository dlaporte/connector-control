import SwiftUI
import ConnectorControlCore
import ConnectorControlState

/// Catalog §3: fields, bindings and layout only; every rule is EditorModel's.
struct EditSheetView: View {
    @StateObject private var model: EditorModel
    @Environment(\.dismiss) private var dismiss
    @State private var hostWindow: NSWindow?
    @FocusState private var envFocus: UUID?

    init(state: AppState, target: EditTarget) {
        _model = StateObject(wrappedValue: EditorModel(state: state, target: target, dialogs: state.dialogs))
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
            HStack {
                if model.canRemove {
                    Button(EditorModel.removeButton, role: .destructive) { model.requestRemove() }
                }
                Spacer()
                Button(AlertDialogs.cancelTitle) { dismiss() }
                Button("Save") { if model.save() { dismiss() } }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!model.canSave)
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
    }

    // MARK: form body

    @ViewBuilder private var formBody: some View {
        Form {
            Section {
                if model.showTypePicker {
                    Picker("Type", selection: $model.isRemote) {
                        Text("Remote").tag(true)
                        Text("Local").tag(false)
                    }
                    .pickerStyle(.segmented)
                    .fixedSize()
                }
                TextField("Name", text: $model.name, prompt: Text("my-mcp"))
            }

            if model.isRemote {
                Section {
                    TextField("Server URL", text: $model.remoteURL,
                               prompt: Text("https://example.com/mcp"))
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
                    TextField("Command", text: $model.command, prompt: Text("npx"))
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
            HStack {
                TextField("argument", text: $row.value)
                    .font(.system(.body, design: .monospaced))
                Button { model.removeArg(id: row.id) } label: {
                    Image(systemName: "xmark.circle")
                }.buttonStyle(.plain)
            }
        }
        Button(EditorModel.addArgumentTitle) { model.addArg() }
            .buttonStyle(.plain).font(.caption).foregroundStyle(.secondary)
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
                            if row.revealed {
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
                            Button { model.toggleReveal(id: row.id) } label: {
                                Image(systemName: "eye")
                            }.buttonStyle(.plain)
                            Button { model.removeEnvRow(id: row.id) } label: {
                                Image(systemName: "xmark.circle")
                            }.buttonStyle(.plain)
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
    }

    @ViewBuilder private var authEditor: some View {
        Picker("Type", selection: $model.authKind) {
            ForEach(RemoteAuthKind.allCases, id: \.self) { kind in
                Text(kind.title).tag(kind)
            }
        }
        .pickerStyle(.menu)

        switch model.authKind {
        case .automatic:
            Text(EditorModel.automaticCaption)
                .font(.caption)
                .foregroundStyle(.secondary)
        case .bearer:
            SecureField("Token", text: $model.bearerToken)
            Text(EditorModel.bearerCaption)
                .font(.caption)
                .foregroundStyle(.secondary)
        case .header:
            TextField("Header name", text: $model.headerName, prompt: Text("X-API-Key"))
            SecureField("Header value", text: $model.headerValue)
        case .oauthClient:
            TextField("Client ID", text: $model.oauthClientID)
            SecureField("Client Secret", text: $model.oauthClientSecret)
            Text(EditorModel.oauthSecretCaption)
                .font(.caption)
                .foregroundStyle(.secondary)
            TextField("Scopes (optional)", text: $model.oauthScopes, prompt: Text("space separated"))
        }
    }

    // MARK: json body

    @ViewBuilder private var jsonBody: some View {
        VStack(alignment: .leading, spacing: 8) {
            // Name lives in the Form's group box; JSON view needs its own so a
            // raw-JSON paste for a new MCP can be named without switching views.
            HStack(spacing: 8) {
                Text("Name")
                TextField("my-mcp", text: $model.name)
                    .textFieldStyle(.roundedBorder)
            }
            WrappingCodeEditor(text: $model.jsonText)
                .frame(maxHeight: .infinity)
                .overlay(RoundedRectangle(cornerRadius: 6)
                    .stroke(model.hasJSONError ? Color.red : Color.secondary.opacity(0.3)))
            Text(model.jsonStatusText)
                .font(.caption)
                .foregroundStyle(model.hasJSONError ? Color.red : Color.secondary)
            if let note = model.toolNote {
                ToolNoteView(note: note)
            }
        }
        .padding(.horizontal, 16)
        .padding(.top, 8)
    }
}

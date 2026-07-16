import SwiftUI
import MCPEnablerCore

struct EditTarget: Identifiable {
    let id: String          // UUID for new, name for existing
    var name: String
    var entry: MCPEntry
    var isNew: Bool

    static func existing(name: String, entry: MCPEntry) -> EditTarget {
        EditTarget(id: name, name: name, entry: entry, isNew: false)
    }

    static func new(template: JSONValue) -> EditTarget {
        EditTarget(id: UUID().uuidString, name: "",
                   entry: MCPEntry(config: template), isNew: true)
    }
}

struct EditSheetView: View {
    @EnvironmentObject var state: AppState
    @Environment(\.dismiss) private var dismiss
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
    @State private var envRevealed: Set<String> = []

    init(target: EditTarget) {
        self.target = target
        _name = State(initialValue: target.name)
        _view = State(initialValue: target.entry.lastEditView)
        let detected = RemotePattern.detect(target.entry.config)
        _isRemote = State(initialValue: detected != nil)
        _remoteURL = State(initialValue: detected ?? "")
        _form = State(initialValue: FormMapper.analyze(target.entry.config).model)
        let data = (try? target.entry.config.serialized()) ?? Data()
        _jsonText = State(initialValue: String(decoding: data, as: UTF8.self))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Picker("View", selection: viewBinding) {
                Text("Form").tag(EditView.form)
                Text("JSON").tag(EditView.json)
            }
            .pickerStyle(.segmented)
            .labelsHidden()

            field("Name") {
                TextField("my-mcp", text: $name).textFieldStyle(.roundedBorder)
            }

            if view == .form { formBody } else { jsonBody }

            if let error = validationError {
                Text(error).font(.callout).foregroundStyle(.red)
            }

            Divider()
            HStack {
                if !target.isNew {
                    Button("Remove…", role: .destructive) { confirmRemove = true }
                }
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Save") { save() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(view == .json && jsonError != nil)
            }
        }
        .padding(16)
        .frame(width: 460)
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
                state.remove(name: target.name)
                dismiss()
            }
        }
    }

    // MARK: view switching

    private var viewBinding: Binding<EditView> {
        Binding(get: { view }, set: { requested in
            guard requested != view else { return }
            if requested == .json {
                syncFormIntoJSON()
                view = .json
            } else {
                attemptSwitchToForm()
            }
        })
    }

    private func attemptSwitchToForm() {
        guard jsonError == nil, let config = parsedJSON() else { return }
        let analysis = FormMapper.analyze(config)
        if analysis.isLossless {
            adoptForm(analysis.model, config: config)
            view = .form
        } else {
            lossWarning = analysis.lost
        }
    }

    private func forceSwitchToForm() {
        guard let config = parsedJSON() else { lossWarning = nil; return }
        adoptForm(FormMapper.analyze(config).model, config: config)
        lossWarning = nil
        view = .form
    }

    private func adoptForm(_ model: FormModel, config: JSONValue) {
        form = model
        let detected = RemotePattern.detect(config)
        isRemote = detected != nil
        remoteURL = detected ?? ""
    }

    private func syncFormIntoJSON() {
        let data = (try? currentFormConfig().serialized()) ?? Data()
        jsonText = String(decoding: data, as: UTF8.self)
        jsonError = nil
    }

    private func parsedJSON() -> JSONValue? {
        do {
            let value = try JSONValue.parse(Data(jsonText.utf8))
            jsonError = nil
            return value
        } catch {
            jsonError = "Not valid JSON: \(error.localizedDescription)"
            return nil
        }
    }

    private func currentFormConfig() -> JSONValue {
        if isRemote {
            guard case .object(var object) = RemotePattern.make(url: remoteURL) else {
                return RemotePattern.make(url: remoteURL)
            }
            for (key, value) in form.additional { object[key] = value }
            return .object(object)
        }
        return FormMapper.serialize(form)
    }

    // MARK: form body

    @ViewBuilder private var formBody: some View {
        if isRemote {
            field("Server URL") {
                TextField("https://example.com/mcp", text: $remoteURL)
                    .textFieldStyle(.roundedBorder)
            }
            Text("Runs via npx mcp-remote — managed for you")
                .font(.caption).foregroundStyle(.secondary)
        } else {
            field("Command") {
                TextField("npx", text: $form.command).textFieldStyle(.roundedBorder)
            }
            field("Arguments") { argsEditor }
            field("Environment variables") { envEditor }
        }
        if !form.additional.isEmpty {
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

    private var additionalPreview: String {
        let data = (try? JSONValue.object(form.additional).serialized()) ?? Data()
        return String(decoding: data, as: UTF8.self)
    }

    private var argsEditor: some View {
        VStack(alignment: .leading, spacing: 4) {
            ForEach(form.args.indices, id: \.self) { index in
                HStack {
                    TextField("argument", text: $form.args[index])
                        .textFieldStyle(.roundedBorder)
                        .font(.system(.body, design: .monospaced))
                    Button { form.args.remove(at: index) } label: {
                        Image(systemName: "xmark.circle")
                    }.buttonStyle(.plain)
                }
            }
            Button("＋ Add argument") { form.args.append("") }
                .buttonStyle(.plain).font(.caption).foregroundStyle(.secondary)
        }
    }

    private var envEditor: some View {
        VStack(alignment: .leading, spacing: 4) {
            ForEach(form.env.keys.sorted(), id: \.self) { key in
                HStack {
                    Text(key).font(.system(.body, design: .monospaced))
                        .frame(width: 130, alignment: .leading)
                    if envRevealed.contains(key) {
                        TextField("value", text: envBinding(key))
                            .textFieldStyle(.roundedBorder)
                            .font(.system(.body, design: .monospaced))
                    } else {
                        SecureField("value", text: envBinding(key))
                            .textFieldStyle(.roundedBorder)
                    }
                    Button {
                        if envRevealed.contains(key) { envRevealed.remove(key) }
                        else { envRevealed.insert(key) }
                    } label: { Image(systemName: "eye") }.buttonStyle(.plain)
                    Button { form.env.removeValue(forKey: key) } label: {
                        Image(systemName: "xmark.circle")
                    }.buttonStyle(.plain)
                }
            }
            EnvAdder { key, value in form.env[key] = value }
        }
    }

    private func envBinding(_ key: String) -> Binding<String> {
        Binding(get: { form.env[key] ?? "" }, set: { form.env[key] = $0 })
    }

    // MARK: json body

    @ViewBuilder private var jsonBody: some View {
        TextEditor(text: $jsonText)
            .font(.system(.callout, design: .monospaced))
            .frame(height: 180)
            .overlay(RoundedRectangle(cornerRadius: 6)
                .stroke(jsonError == nil ? Color.secondary.opacity(0.3) : .red))
            .onChange(of: jsonText) { _ = parsedJSON() }
        if let error = jsonError {
            Text(error).font(.caption).foregroundStyle(.red)
        } else {
            Text("Tip: paste a README snippet — a {\"mcpServers\": {…}} wrapper is unwrapped automatically.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    // MARK: save

    private func save() {
        validationError = nil
        var config: JSONValue
        if view == .json {
            guard var parsed = parsedJSON() else { return }
            // Unwrap {"mcpServers": {"name": {...}}} paste convenience.
            if case .object(let outer) = parsed, outer.count == 1,
               case .object(let inner)? = outer["mcpServers"], inner.count == 1,
               let (pastedName, pastedConfig) = inner.first {
                if name.trimmingCharacters(in: .whitespaces).isEmpty { name = pastedName }
                parsed = pastedConfig
            }
            config = parsed
        } else {
            if isRemote {
                guard let url = URL(string: remoteURL),
                      let scheme = url.scheme?.lowercased(),
                      scheme == "http" || scheme == "https", url.host != nil else {
                    validationError = "Server URL must be a valid http(s) URL."
                    return
                }
            } else if form.command.trimmingCharacters(in: .whitespaces).isEmpty {
                validationError = "Command must not be empty."
                return
            }
            config = currentFormConfig()
        }
        let entry = MCPEntry(enabled: target.entry.enabled, config: config,
                             lastEditView: view)
        if let error = state.upsert(name: name, entry: entry,
                                    renamedFrom: target.isNew ? nil : target.name) {
            validationError = error
            return
        }
        dismiss()
    }

    private func field(_ label: String, @ViewBuilder content: () -> some View) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(label.uppercased()).font(.caption2).foregroundStyle(.secondary)
            content()
        }
    }
}

/// Two fields + button for adding an env var.
struct EnvAdder: View {
    var onAdd: (String, String) -> Void
    @State private var key = ""
    @State private var value = ""

    var body: some View {
        HStack {
            TextField("NAME", text: $key)
                .textFieldStyle(.roundedBorder).frame(width: 130)
                .font(.system(.body, design: .monospaced))
            TextField("value", text: $value).textFieldStyle(.roundedBorder)
            Button("＋") {
                let k = key.trimmingCharacters(in: .whitespaces)
                guard !k.isEmpty else { return }
                onAdd(k, value)
                key = ""; value = ""
            }.buttonStyle(.plain)
        }
    }
}

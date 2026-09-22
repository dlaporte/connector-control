import SwiftUI
import AppKit
import UniformTypeIdentifiers
import ConnectorControlState

/// The Publish sheet and, with one flag flipped, the Export sheet: layout, bindings and the two
/// native panels. Every rule and string is PublishModel's; the preview under the ticks is the
/// document itself, so nothing here filters, elides or reformats it.
struct PublishSheetView: View {
    /// Publishing binds the collection to a folder it rewrites on every change; exporting writes
    /// the same document once, wherever the save panel says, and binds nothing. The sheet is the
    /// same sheet because the decision the author is making — what travels — is the same one.
    enum Mode {
        case publish
        case export
    }

    @ObservedObject var model: PublishModel
    let mode: Mode
    let onDone: () -> Void

    /// What publish() or export(to:) answered. The model hands the message back rather than
    /// publishing a state for it, so the sheet holds it for as long as it is on screen.
    @State private var failure: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title).font(.headline)

            if mode == .publish {
                folderRow
            }

            if !model.envRows.isEmpty || !model.pathRows.isEmpty {
                ScrollView {
                    VStack(alignment: .leading, spacing: 10) {
                        if !model.envRows.isEmpty {
                            section(PublishModel.envSectionTitle) { envRows }
                        }
                        if !model.pathRows.isEmpty {
                            section(PublishModel.pathsSectionTitle) { pathRows }
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 200)
            }

            preview

            ForEach(model.warnings, id: \.self) { warning in
                Text(warning)
                    .font(.caption)
                    .foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if let failure {
                Text(failure)
                    .font(.callout)
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Divider()
            footer
        }
        .padding(16)
        .frame(width: 580)
    }

    /// Publishing names the collection it binds; exporting borrows the menu item's own wording,
    /// which names the collection it writes once.
    private var title: String {
        mode == .publish ? model.title : PopoverModel.exportTitleFor(model.collection)
    }

    // MARK: folder

    @ViewBuilder private var folderRow: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 8) {
                if let folder = model.folder, !folder.isEmpty {
                    Text(folder)
                        .font(.system(.caption, design: .monospaced))
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                Button(PopoverModel.chooseFolderButton) { chooseFolder() }
            }
            Text(model.folderLine)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    // MARK: rows

    @ViewBuilder private func section<Content: View>(_ heading: String,
                                                    @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(heading)
                .font(.subheadline)
                .fontWeight(.semibold)
            content()
        }
    }

    @ViewBuilder private var envRows: some View {
        ForEach($model.envRows) { $row in
            HStack(spacing: 8) {
                Text(row.connector)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Text(row.name)
                    .font(.system(.callout, design: .monospaced))
                Spacer()
                Toggle(PublishModel.shareValueLabel, isOn: $row.share)
                    .toggleStyle(.checkbox)
                    .font(.caption)
                // A stripped value travels as a name and a hint; a shared one needs no hint,
                // and the column keeps its width so ticking a row moves nothing else.
                Group {
                    if !row.share {
                        TextField(PublishModel.hintPlaceholder, text: $row.hint)
                            .textFieldStyle(.roundedBorder)
                            .font(.caption)
                    }
                }
                .frame(width: 200)
            }
        }
    }

    @ViewBuilder private var pathRows: some View {
        ForEach($model.pathRows) { $row in
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 8) {
                    // The tick carries no label of its own: the row it is on says what marking it
                    // would replace, and the name and hint fields appear under it once it is on.
                    Toggle("", isOn: $row.marked)
                        .toggleStyle(.checkbox)
                        .labelsHidden()
                    Text(row.connector)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Text(row.pointer.description)
                        .font(.system(.caption, design: .monospaced))
                        .foregroundStyle(.secondary)
                    Text(row.value)
                        .font(.system(.caption, design: .monospaced))
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Spacer()
                }
                if row.marked {
                    HStack(spacing: 8) {
                        TextField(PublishModel.pathNamePlaceholder, text: $row.name)
                            .textFieldStyle(.roundedBorder)
                            .font(.caption)
                            .frame(width: 150)
                        TextField(PublishModel.hintPlaceholder, text: $row.hint)
                            .textFieldStyle(.roundedBorder)
                            .font(.caption)
                            .frame(width: 200)
                    }
                    .padding(.leading, 20)
                }
            }
        }
    }

    // MARK: preview

    @ViewBuilder private var preview: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(PublishModel.previewTitle)
                .font(.subheadline)
                .fontWeight(.semibold)
            // Selectable text, not an editor: the document is the model's, and there is nothing
            // here the author could usefully type into.
            ScrollView([.horizontal, .vertical]) {
                Text(model.preview)
                    .font(.system(.caption, design: .monospaced))
                    .textSelection(.enabled)
                    .padding(6)
            }
            .frame(height: 170)
            .frame(maxWidth: .infinity, alignment: .leading)
            .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.secondary.opacity(0.3)))
        }
    }

    // MARK: footer

    @ViewBuilder private var footer: some View {
        HStack {
            Text(model.footerLine)
                .font(.system(.caption, design: .monospaced))
                .foregroundStyle(.secondary)
            Spacer()
            Button(ImportModel.cancelButton) { onDone() }
            if mode == .publish {
                Button(PublishModel.publishButton) { publish() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!model.canPublish)
            } else {
                Button(PublishModel.exportButton) { export() }
                    .keyboardShortcut(.defaultAction)
            }
        }
    }

    // MARK: panels

    /// Settings ▸ Storage's panel, for a folder instead of the store: one directory, handed
    /// straight to the model.
    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = "Choose"
        if panel.runModal() == .OK, let url = panel.url {
            model.folder = url.path
            failure = nil
        }
    }

    private func publish() {
        failure = model.publish()
        if failure == nil { onDone() }
    }

    private func export() {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = model.fileName
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        failure = model.export(to: url.path)
        if failure == nil { onDone() }
    }
}

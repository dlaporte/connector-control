import SwiftUI
import ConnectorControlState

/// The Publish sheet and, in the model's other mode, the Export sheet: layout, bindings and the
/// two native panels, which are FilePanels'. Every rule and string is PublishModel's — the mode
/// included, and with it the title, the gate and the verb; the preview under the ticks is the
/// document itself, so nothing here filters, elides or reformats it.
struct PublishSheetView: View {
    @ObservedObject var model: PublishModel
    let onDone: () -> Void

    /// What publish() or export(to:) answered. The model hands the message back rather than
    /// publishing a state for it, so the sheet holds it for as long as it is on screen.
    @State private var failure: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(model.sheetTitle).font(.headline)

            // Export asks the save panel for a path when its button is pressed, so it has no
            // folder row.
            if model.mode == .publish {
                folderRow
            }

            if model.hasEnvRows || model.hasPathRows {
                ScrollView {
                    VStack(alignment: .leading, spacing: 10) {
                        if model.hasEnvRows {
                            section(PublishModel.envSectionTitle) { envRows }
                        }
                        if model.hasPathRows {
                            section(PublishModel.pathsSectionTitle) { pathRows }
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 200)
            }

            unanswered

            preview

            // One line per credential-looking value, and a connector that passes keys as
            // arguments can produce many: the list scrolls rather than pushing the footer
            // buttons off a sheet whose height is its content's.
            if !model.warnings.isEmpty {
                ScrollView {
                    VStack(alignment: .leading, spacing: 2) {
                        ForEach(model.warnings, id: \.self) { warning in
                            Text(warning)
                                .font(.caption)
                                .foregroundStyle(.orange)
                                .fixedSize(horizontal: false, vertical: true)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                    }
                }
                .frame(maxHeight: 72)
            }

            if let failure { FailureLine(failure) }

            Divider()
            footer
        }
        .padding(16)
        // Wide enough for a path row's value beside its fields, and growable from there, so a
        // long path can be read in full rather than truncated for good. Same width as the
        // Windows dialog.
        .frame(minWidth: 620, idealWidth: 620, maxWidth: .infinity)
    }

    // MARK: folder

    @ViewBuilder private var folderRow: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 8) {
                if let folder = model.folder, !folder.isEmpty {
                    // Elided in the middle, as the footer is; the Windows dialog cuts the tail of
                    // both, since WPF has no middle ellipsis.
                    Text(folder)
                        .font(.system(.caption, design: .monospaced))
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                Button(PublishModel.chooseFolderButton) { chooseFolder() }
            }
            Text(model.folderLine)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    // MARK: what holds the sheet

    /// Every entry that holds Publish and Export: a path mark this sheet could not place, and a
    /// path this machine keeps back that the document would carry as written. Each line is the
    /// reason and its way out besides a tick. Outside the rows' scroll region so the explanation
    /// of a disabled button is never scrolled away, and capped so the footer stays on screen. A
    /// connector that no longer exists has no row to tick, so its line shows even when the
    /// sections above are empty. Each list is read once per render: the kept paths are found by
    /// rendering the document.
    @ViewBuilder private var unanswered: some View {
        let marks = model.unresolvedMarks
        let kept = model.keptPaths
        if !marks.isEmpty || !kept.isEmpty {
            ScrollView {
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(marks) { mark in
                        unansweredLine(PublishModel.unresolvedMarkNote(mark.connector, mark.name)) {
                            Button(PublishModel.forgetMarkButton) { model.forgetUnresolvedMark(mark.id) }
                        }
                    }
                    // The note is the model's for each entry, never composed here: whether a folder
                    // can be rewritten from this sheet, and whose folder it is, are facts the sheet
                    // does not hold. A folder of this collection's own is answered by writing the
                    // token in its place, never by releasing it, since released it would travel as
                    // written; anything else is answered by Release. Both answers hand back the
                    // entry's note when they refuse, which is what the failure line then says.
                    ForEach(kept) { path in
                        unansweredLine(model.note(for: path)) {
                            switch path.kind {
                            case .folder:
                                Button(PublishModel.useDirectoryTokenButton) { failure = model.useDirectoryToken(path) }
                            case .path:
                                Button(PublishModel.releaseValueButton) { failure = model.releaseKeptPath(path.value) }
                            }
                        }
                    }
                }
            }
            .frame(maxHeight: 96)
        }
    }

    /// One entry's line: its note in caution, and its answers at the trailing edge. The answers
    /// are a list of their own, so an entry that offers a second kind of answer adds a button here
    /// and changes nothing else.
    @ViewBuilder private func unansweredLine<Answers: View>(_ note: String,
                                                           @ViewBuilder answers: () -> Answers) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text(note)
                .font(.caption)
                .foregroundStyle(.orange)
                .fixedSize(horizontal: false, vertical: true)
            Spacer()
            HStack(spacing: 6) { answers() }
                .controlSize(.small)
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
                // Variable names are short by convention but the author's to write, so a long one
                // trims rather than crowding out what follows it.
                Text(row.name)
                    .font(.system(.callout, design: .monospaced))
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .frame(maxWidth: 180, alignment: .leading)
                Spacer()
                Toggle(PublishModel.shareValueLabel, isOn: $row.share)
                    .toggleStyle(.checkbox)
                    .font(.caption)
                // One slot, two states: a stripped value travels as its name and a hint, so the
                // hint field is here; a shared one travels as itself, so the value is, and the
                // tick is a decision made with the bytes it would send in view. The width is
                // fixed, so ticking a row moves nothing else.
                Group {
                    if row.share {
                        Text(row.value)
                            .font(.system(.caption, design: .monospaced))
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                            .truncationMode(.middle)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    } else {
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
                    // The tick carries no visible label: the row it is on says what marking it
                    // would replace, and the name and hint fields appear under it once it is on.
                    // VoiceOver gets the sentence the label would have been.
                    Toggle("", isOn: $row.marked)
                        .toggleStyle(.checkbox)
                        .labelsHidden()
                        .accessibilityLabel(PublishModel.markPathLabel)
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
                .lineLimit(1)
                .truncationMode(.middle)
            Spacer()
            Button(PublishModel.cancelButton) { onDone() }
                .keyboardShortcut(.cancelAction)
            Button(model.mode == .publish ? PublishModel.publishButton : PublishModel.exportButton) { finish() }
                .keyboardShortcut(.defaultAction)
                .disabled(!model.canFinish)
        }
    }

    // MARK: verbs

    /// One directory, handed straight to the model.
    private func chooseFolder() {
        guard let folder = FilePanels.chooseFolder() else { return }
        model.folder = folder
        failure = nil
    }

    /// The model's verb. An export first asks the save panel where to write, and a panel
    /// cancelled writes nothing.
    private func finish() {
        var path: String?
        if model.mode == .export {
            guard let chosen = FilePanels.saveCollectionDocument(named: model.fileName) else { return }
            path = chosen
        }
        failure = model.finish(path: path)
        if failure == nil { onDone() }
    }
}

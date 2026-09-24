import SwiftUI
import ConnectorControlState

/// The Import sheet: the document the window's file panel opened, the two exclusive things that
/// can be done with it, and a row per connector saying what would happen to it. Layout and
/// bindings only; every rule and string is ImportModel's.
struct ImportSheetView: View {
    @ObservedObject var model: ImportModel
    let onDone: () -> Void

    /// What perform() answered. The model hands the message back rather than publishing a state
    /// for it, so the sheet holds it for as long as it is on screen.
    @State private var failure: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(ImportModel.title).font(.headline)
            source

            if let loadError = model.loadError {
                // A document this app cannot read offers nothing to choose between: the modes,
                // the rows and Import all go, and Cancel is the only way out.
                FailureLine(loadError)
            } else {
                copiesCard
                syncCard
            }

            if let failure { FailureLine(failure) }

            Divider()
            footer
        }
        .padding(16)
        // Wide enough for a row's badge and its Replace picker beside the name, and growable from
        // there, so a long skipped-reason can be read in full. Same width as the Windows dialog.
        .frame(minWidth: 620, idealWidth: 620, maxWidth: .infinity)
    }

    // MARK: source

    /// The file the sheet was opened with, and what that file says about itself.
    @ViewBuilder private var source: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(model.path)
                .font(.system(.caption, design: .monospaced))
                .lineLimit(1)
                .truncationMode(.middle)
            Text(model.sourceLine)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    // MARK: modes

    /// One of the two exclusive cards. Only the header is a button: the picker, the ticks and the
    /// name field inside a card are controls of their own, and a button wrapping them would
    /// swallow their clicks. The card's own contents arrive knowing whether it is the chosen one,
    /// so each card decides for itself what it shows when it is not.
    private func card<Content: View>(_ mode: ImportModel.Mode, _ title: String,
                                     @ViewBuilder content: @escaping (Bool) -> Content) -> some View {
        let selected = model.mode == mode
        return VStack(alignment: .leading, spacing: 6) {
            Button {
                model.mode = mode
                failure = nil
            } label: {
                HStack(spacing: 6) {
                    Image(systemName: selected ? "largecircle.filled.circle" : "circle")
                        .foregroundStyle(selected ? Color.accentColor : Color.secondary)
                    Text(title)
                        .fontWeight(.medium)
                        .fixedSize(horizontal: false, vertical: true)
                        .multilineTextAlignment(.leading)
                    Spacer(minLength: 0)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityAddTraits(selected ? AccessibilityTraits.isSelected : [])

            content(selected)
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 8)
            .fill(selected ? Color.accentColor.opacity(0.08) : Color.clear))
        .overlay(RoundedRectangle(cornerRadius: 8)
            .stroke(selected ? Color.accentColor : Color.secondary.opacity(0.3)))
    }

    /// Copies into a collection the user owns: which one, and what would happen to each connector.
    @ViewBuilder private var copiesCard: some View {
        card(.addToCollection, ImportModel.addModeTitle(model.targetCollection)) { selected in
            Text(ImportModel.addModeDetail)
                .font(.caption)
                .foregroundStyle(.secondary)
            if selected {
                // The title names the collection the copies would land in; this changes it.
                Picker("", selection: $model.targetCollection) {
                    ForEach(model.localCollections, id: \.self) { name in
                        Text(name).tag(name)
                    }
                }
                .labelsHidden()
                .pickerStyle(.menu)
                .fixedSize()
                .accessibilityLabel(ImportModel.addModeTitle(model.targetCollection))
                ScrollView {
                    VStack(alignment: .leading, spacing: 4) { rows }
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 220)
            }
        }
    }

    /// A collection of its own, bound to the file: what to call it.
    @ViewBuilder private var syncCard: some View {
        card(.keepInSync, ImportModel.syncModeTitle) { selected in
            if selected {
                TextField("", text: $model.syncName)
                    .textFieldStyle(.roundedBorder)
                    .labelsHidden()
                    .accessibilityLabel(ImportModel.syncNameLabel)
            }
            Text(ImportModel.syncModeDetail)
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    // MARK: rows

    @ViewBuilder private var rows: some View {
        ForEach($model.rows) { $row in
            HStack(spacing: 8) {
                Toggle("", isOn: $row.include)
                    .toggleStyle(.checkbox)
                    .labelsHidden()
                    // A connector this platform has no way to run cannot be imported at all.
                    .disabled(row.excludedReason != nil)
                    .accessibilityLabel(ImportModel.includeLabel(row.name))
                Text(row.name).lineLimit(1)
                if let caution = row.needsCaution {
                    Image(systemName: PopoverModel.toolWarningGlyph)
                        .imageScale(.small)
                        .foregroundStyle(.orange)
                        .help(caution)
                        .accessibilityLabel(caution)
                }
                Spacer(minLength: 8)
                badge(for: $row)
            }
        }
    }

    /// What would happen to this connector: how the collision it would cause resolves, or — for
    /// every other row, and for a collision that is not coming across — the row's own badge.
    /// Unticking a collision and choosing Skip in the picker mean the same thing, so a row that
    /// is not coming across says so instead of offering the choice.
    @ViewBuilder private func badge(for row: Binding<ImportModel.Row>) -> some View {
        if row.wrappedValue.showsPicker {
            HStack(spacing: 6) {
                CollisionPicker(choice: row.choice, connector: row.wrappedValue.name)
                if row.wrappedValue.choice == .replace {
                    caption(ImportModel.replaceKeepsValues)
                }
            }
        } else {
            // The badge is a row's whole point and the skipped reason is the longest of them, so
            // the one line that shows it carries the full sentence as its tooltip.
            caption(row.wrappedValue.badge)
                .help(row.wrappedValue.badge)
        }
    }

    @ViewBuilder private func caption(_ text: String) -> some View {
        Text(text)
            .font(.caption)
            .foregroundStyle(.secondary)
            .lineLimit(1)
            .truncationMode(.tail)
    }

    // MARK: footer

    @ViewBuilder private var footer: some View {
        HStack {
            Spacer()
            Button(ImportModel.cancelButton) { onDone() }
                .keyboardShortcut(.cancelAction)
            // Gone rather than dimmed on a document this app cannot read, as the Windows dialog
            // collapses it: there is nothing to import, and Cancel is the only way out.
            if model.loadError == nil {
                Button(ImportModel.importButton(model.importCount)) { perform() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!model.canImport)
            }
        }
    }

    private func perform() {
        failure = model.perform()
        if failure == nil { onDone() }
    }
}

/// How one clashing connector lands: the Import sheet's picker, which the Copy sheet asks with too.
struct CollisionPicker: View {
    @Binding var choice: ImportChoice
    let connector: String

    var body: some View {
        Picker("", selection: $choice) {
            ForEach(ImportModel.collisionChoices, id: \.self) { choice in
                Text(ImportModel.choiceTitle(choice)).tag(choice)
            }
        }
        .labelsHidden()
        .pickerStyle(.menu)
        .fixedSize()
        // Not the row's name: the tick beside it on the Import sheet already answers to that,
        // and a screen reader would announce the two controls identically.
        .accessibilityLabel(ImportModel.collisionPickerLabel(connector))
    }
}

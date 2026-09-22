import SwiftUI
import ConnectorControlState

/// The Review & Apply sheet: what one synced collection's source would change, connector by
/// connector, with the JSON both sides of every change. Layout and bindings only; every rule and
/// string is ReviewModel's.
struct ReviewSheetView: View {
    @ObservedObject var model: ReviewModel
    let onDone: () -> Void

    /// The order the rows already arrive in, which is the order the summary sentence reads in.
    private static let kinds: [ReviewModel.Kind] = [.added, .removed, .changed]

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(model.title).font(.headline)
            Text(model.summary)
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    ForEach(ReviewSheetView.kinds, id: \.self) { kind in
                        group(kind)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(height: 320)

            if model.sourceMoved { moved }

            Divider()
            footer
        }
        .padding(16)
        // Wide enough for two columns of JSON side by side, and growable from there, so a long
        // line can be read without scrolling its column. Same width as the Windows dialog.
        .frame(minWidth: 760, idealWidth: 760, maxWidth: .infinity)
    }

    // MARK: changes

    /// The rows of one kind under their heading, or nothing at all when this update has none.
    private func group(_ kind: ReviewModel.Kind) -> some View {
        let rows = model.rows.filter { $0.kind == kind }
        return VStack(alignment: .leading, spacing: 8) {
            if !rows.isEmpty {
                Text(ReviewSheetView.label(for: kind))
                    .font(.subheadline)
                    .fontWeight(.semibold)
                ForEach(rows) { row in
                    change(row)
                }
            }
        }
    }

    /// The three headings, in the model's words. ReviewModel carries one static per kind rather
    /// than a mapping, so the sheet is where the pairing lives.
    private static func label(for kind: ReviewModel.Kind) -> String {
        switch kind {
        case .added: return ReviewModel.addedLabel
        case .removed: return ReviewModel.removedLabel
        case .changed: return ReviewModel.changedLabel
        }
    }

    private func change(_ row: ReviewModel.Row) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(row.name).fontWeight(.medium)
            HStack(alignment: .top, spacing: 8) {
                side(row.before)
                side(row.after)
            }
        }
    }

    /// One side of a change, as the editor would show it. A side that does not exist — nothing
    /// before an addition, nothing after a removal — still holds its column, so the side that
    /// does stays where the eye left it on the row above.
    private func side(_ text: String?) -> some View {
        ScrollView(.horizontal) {
            Text(text ?? "")
                .font(.system(.caption, design: .monospaced))
                .textSelection(.enabled)
                .padding(6)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .frame(height: 120)
        .frame(maxWidth: .infinity)
        .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.secondary.opacity(0.3)))
    }

    // MARK: source moved

    /// The document changed while the sheet was open, so what is listed is no longer what would
    /// land. Refresh rebuilds the rows from the file as it is now.
    @ViewBuilder private var moved: some View {
        HStack(spacing: 8) {
            Image(systemName: PopoverModel.toolWarningGlyph)
                .imageScale(.small)
                .foregroundStyle(.orange)
            Text(ReviewModel.sourceMovedMessage)
                .font(.caption)
                .foregroundStyle(.orange)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 8)
            Button(CollectionsModel.refreshButton) { model.refresh() }
        }
    }

    // MARK: footer

    @ViewBuilder private var footer: some View {
        HStack {
            Spacer()
            Button(ImportModel.cancelButton) { onDone() }
            Button(ReviewModel.applyButton) { apply() }
                .keyboardShortcut(.defaultAction)
        }
    }

    private func apply() {
        // The one thing that refuses is a document that changed under the sheet, and the caution
        // line above the footer is what says so.
        if model.apply() { onDone() }
    }
}

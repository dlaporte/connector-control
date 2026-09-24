import SwiftUI
import ConnectorControlState

/// The Review & Apply sheet: what one synced collection's source would change, connector by
/// connector, with the JSON both sides of every change. Layout and bindings only; every rule and
/// string is ReviewModel's.
struct ReviewSheetView: View {
    @ObservedObject var model: ReviewModel
    let onDone: () -> Void

    /// What apply() answered. The model hands the message back rather than publishing a state for
    /// it, so the sheet holds it for as long as it is on screen.
    @State private var failure: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(model.title).font(.headline)
            Text(model.summary)
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    ForEach(presentKinds, id: \.self) { kind in
                        group(kind)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            // A cap, not a height: a one-connector update is a short sheet, as it is on Windows.
            .frame(maxHeight: 320)

            if model.sourceMoved { moved }

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
        // Wide enough for two columns of JSON side by side, and growable from there, so a long
        // line can be read without scrolling its column. Same width as the Windows dialog.
        .frame(minWidth: 760, idealWidth: 760, maxWidth: .infinity)
    }

    // MARK: changes

    /// The kinds this update actually has, in the model's order. Filtered before the list rather
    /// than inside each group, so a kind with nothing in it takes no heading and no spacing.
    private var presentKinds: [ReviewModel.Kind] {
        ReviewModel.kinds.filter { kind in model.rows.contains { $0.kind == kind } }
    }

    /// The rows of one kind under their heading.
    private func group(_ kind: ReviewModel.Kind) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(ReviewModel.kindLabel(kind))
                .font(.subheadline)
                .fontWeight(.semibold)
            ForEach(model.rows.filter { $0.kind == kind }) { row in
                change(row)
            }
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
        // Both directions: a connector's config runs past seven caption lines as readily as it
        // runs past the column's width, and this is the surface whose whole purpose is reading it.
        ScrollView([.horizontal, .vertical]) {
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
            Button(ReviewModel.refreshButton) {
                model.refresh()
                failure = nil
            }
        }
    }

    // MARK: footer

    @ViewBuilder private var footer: some View {
        HStack {
            Spacer()
            Button(ReviewModel.cancelButton) { onDone() }
                .keyboardShortcut(.cancelAction)
            Button(ReviewModel.applyButton) { apply() }
                .keyboardShortcut(.defaultAction)
        }
    }

    private func apply() {
        let message = model.apply()
        // A document that changed under the sheet already has the caution line above the footer,
        // carrying this very sentence and the Refresh button that answers it; it is not said twice.
        failure = model.sourceMoved ? nil : message
        if message == nil { onDone() }
    }
}

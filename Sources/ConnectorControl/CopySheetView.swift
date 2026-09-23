import SwiftUI
import ConnectorControlState

/// The Copy sheet: the ticked connectors against the collection they are going to, asking only
/// about the ones it already holds. Layout and bindings only; every rule and string is
/// CopyModel's, and the collision picker is the Import sheet's.
struct CopySheetView: View {
    @ObservedObject var model: CopyModel
    /// The refusal a failed copy leaves in the window's model, which this sheet cannot reach
    /// through CopyModel: the copy is the window model's verb, and so is its reason.
    let refusal: () -> String?
    let onDone: () -> Void

    /// Why the copy did not land, held for as long as the sheet is on screen.
    @State private var failure: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(CopyModel.title(model.destination)).font(.headline)

            ScrollView {
                VStack(alignment: .leading, spacing: 4) { rows }
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxHeight: 220)

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
        .frame(minWidth: 480, idealWidth: 480, maxWidth: .infinity)
    }

    // MARK: rows

    @ViewBuilder private var rows: some View {
        ForEach($model.rows) { $row in
            HStack(spacing: 8) {
                Text(row.name).lineLimit(1)
                Spacer(minLength: 8)
                outcome(for: $row)
            }
        }
    }

    /// A clash shows the Import sheet's picker; a clean arrival shows its badge, which a clashing
    /// row leaves empty for the picker to answer instead.
    @ViewBuilder private func outcome(for row: Binding<CopyModel.Row>) -> some View {
        if row.wrappedValue.clashes {
            Picker("", selection: row.choice) {
                ForEach(ImportModel.collisionChoices, id: \.self) { choice in
                    Text(ImportModel.choiceTitle(choice)).tag(choice)
                }
            }
            .labelsHidden()
            .pickerStyle(.menu)
            .fixedSize()
            .accessibilityLabel(ImportModel.collisionPickerLabel(row.wrappedValue.name))
        } else if !row.wrappedValue.badge.isEmpty {
            Text(row.wrappedValue.badge)
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
        }
    }

    // MARK: footer

    @ViewBuilder private var footer: some View {
        HStack {
            Spacer()
            Button(ImportModel.cancelButton) { onDone() }
            Button(CopyModel.copyButton) { perform() }
                .keyboardShortcut(.defaultAction)
        }
    }

    /// A copy that did not land with nothing to say for itself — the ticks went while the sheet
    /// was up — has nothing left to ask either, so the sheet goes.
    private func perform() {
        failure = model.perform() ? nil : refusal()
        if failure == nil { onDone() }
    }
}

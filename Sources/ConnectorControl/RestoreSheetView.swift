import SwiftUI
import ConnectorControlCore
import ConnectorControlState

/// Catalog §5: layout only; every rule and string is RestoreModel's.
struct RestoreSheetView: View {
    @StateObject private var model: RestoreModel
    @Environment(\.dismiss) private var dismiss

    init(state: AppState) {
        _model = StateObject(wrappedValue: RestoreModel(state: state))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(RestoreModel.headline).font(.headline)
            Text(RestoreModel.caption)
                .font(.caption).foregroundStyle(.secondary)
            List(model.backups, id: \.self, selection: $model.selection) { url in
                Text(url.lastPathComponent).font(.system(.callout, design: .monospaced))
            }
            .frame(height: 180)
            if let restoreError = model.restoreError {
                Text(restoreError).font(.callout).foregroundStyle(.red)
            }
            HStack {
                Spacer()
                Button(RestoreModel.cancelTitle) { dismiss() }
                Button(RestoreModel.restoreTitle) { model.requestRestore() }
                    .disabled(!model.canRestore)
            }
        }
        .padding(16)
        .frame(width: 460)
        .onAppear { model.load() }
        .confirmationDialog(
            model.confirmMessage,
            isPresented: Binding(get: { model.confirming },
                                 set: { if !$0 { model.cancelRestore() } }),
            titleVisibility: .visible
        ) {
            Button(RestoreModel.restoreButton, role: .destructive) {
                if model.confirmRestore() { dismiss() }
            }
        }
    }
}

import SwiftUI
import ConnectorControlState

/// Layout only; every rule and string is RestoreModel's (the sheet and its Claude's config tab) or
/// CollectionRestoreModel's (its Collections tab).
struct RestoreSheetView: View {
    private enum Tab { case collections, claudeConfig }

    @StateObject private var model: RestoreModel
    @StateObject private var collections: CollectionRestoreModel
    @State private var tab = Tab.collections
    @Environment(\.dismiss) private var dismiss

    init(state: AppState) {
        _model = StateObject(wrappedValue: RestoreModel(state: state))
        _collections = StateObject(wrappedValue: CollectionRestoreModel(state: state))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(RestoreModel.headline).font(.headline)
            // Platform idiom: a segmented picker switches the tabs; Windows uses a TabControl styled as
            // Settings' own tabs. The headline names it for VoiceOver.
            Picker(RestoreModel.headline, selection: $tab) {
                Text(RestoreModel.collectionsTab).tag(Tab.collections)
                Text(RestoreModel.claudeConfigTab).tag(Tab.claudeConfig)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .fixedSize()
            switch tab {
            case .collections: collectionsTab
            case .claudeConfig: claudeConfigTab
            }
            HStack {
                Spacer()
                Button(RestoreModel.cancelTitle) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(RestoreModel.restoreTitle) {
                    switch tab {
                    case .collections: collections.requestRestore()
                    case .claudeConfig: model.requestRestore()
                    }
                }
                .disabled(!(tab == .collections ? collections.canRestore : model.canRestore))
            }
        }
        .padding(16)
        .frame(width: 460)
        // Both tabs read their backups once, when the sheet opens, not each time a tab is shown.
        .onAppear {
            collections.load()
            model.load()
        }
    }

    private var collectionsTab: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(CollectionRestoreModel.caption)
                .font(.caption).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            List(collections.backups, selection: $collections.selection) { backup in
                Text(backup.label).font(.system(.callout, design: .monospaced))
            }
            .frame(height: 120)
            List(collections.rows, selection: $collections.collection) { row in
                HStack {
                    Text(row.name).lineLimit(1)
                    Spacer()
                    // The name gives way to a long detail, as on Windows, where the detail's column is sized to it.
                    Text(row.detail).foregroundStyle(.secondary).lineLimit(1).layoutPriority(1)
                }
                .accessibilityElement(children: .ignore)
                .accessibilityLabel(row.spokenName)
            }
            .frame(height: 120)
            if let restoreError = collections.restoreError {
                FailureLine(restoreError)
            }
        }
        .confirmationDialog(
            collections.confirmMessage,
            isPresented: Binding(get: { collections.confirming },
                                 set: { if !$0 { collections.cancelRestore() } }),
            titleVisibility: .visible
        ) {
            Button(RestoreModel.restoreButton, role: .destructive) {
                if collections.confirmRestore() { dismiss() }
            }
        } message: {
            if let informative = collections.confirmInformative { Text(informative) }
        }
    }

    private var claudeConfigTab: some View {
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 4) {
                Text(RestoreModel.caption)
                Text(RestoreModel.claudeConfigNote)
            }
            .font(.caption).foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
            List(model.backups, id: \.self, selection: $model.selection) { url in
                Text(url.lastPathComponent).font(.system(.callout, design: .monospaced))
            }
            .frame(height: 180)
            if let restoreError = model.restoreError {
                FailureLine(restoreError)
            }
        }
        .confirmationDialog(
            model.confirmMessage,
            isPresented: Binding(get: { model.confirming },
                                 set: { if !$0 { model.cancelRestore() } }),
            titleVisibility: .visible
        ) {
            Button(RestoreModel.restoreButton, role: .destructive) {
                if model.confirmRestore() { dismiss() }
            }
        } message: {
            if let informative = model.confirmInformative { Text(informative) }
        }
    }
}

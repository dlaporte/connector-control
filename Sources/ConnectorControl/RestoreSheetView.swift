import SwiftUI
import ConnectorControlState

/// Layout only; every rule and string is RestoreModel's (the sheet and its Claude's config tab) or
/// CollectionRestoreModel's (its Collections tab).
struct RestoreSheetView: View {
    enum Tab { case collections, claudeConfig }

    /// The tab body's one height, whichever tab is shown and whatever it shows, so switching tabs, choosing a
    /// backup or a failure line appearing never resizes the sheet or moves the switch or the buttons. Inside it
    /// the lists take what the text leaves: at the default text size each list on Collections shows at least
    /// five rows (RestoreSheetLayoutTests measures it).
    static let bodyHeight: CGFloat = 340

    @StateObject private var model: RestoreModel
    @StateObject private var collections: CollectionRestoreModel
    @State private var tab: Tab
    @Environment(\.dismiss) private var dismiss

    init(state: AppState) {
        _model = StateObject(wrappedValue: RestoreModel(state: state))
        _collections = StateObject(wrappedValue: CollectionRestoreModel(state: state))
        _tab = State(initialValue: .collections)
    }

    /// The sheet over models the caller holds, open on `tab`: RestoreSheetLayoutTests lays it out in each
    /// state it can be in.
    init(model: RestoreModel, collections: CollectionRestoreModel, tab: Tab = .collections) {
        _model = StateObject(wrappedValue: model)
        _collections = StateObject(wrappedValue: collections)
        _tab = State(initialValue: tab)
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
            Group {
                switch tab {
                case .collections: collectionsTab
                case .claudeConfig: claudeConfigTab
                }
            }
            .frame(height: RestoreSheetView.bodyHeight, alignment: .top)
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
            // The two lists share what the caption and a failure line leave, evenly.
            headed(CollectionRestoreModel.backupsHeading) {
                List(collections.backups, selection: $collections.selection) { backup in
                    Text(backup.label).font(.system(.callout, design: .monospaced))
                }
                .listStyle(.bordered)
            }
            headed(CollectionRestoreModel.collectionsHeading) {
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
                .listStyle(.bordered)
                .overlay {
                    if collections.selection == nil {
                        Text(CollectionRestoreModel.chooseBackupPlaceholder)
                            .foregroundStyle(.secondary)
                            .multilineTextAlignment(.center)
                            .padding()
                    }
                }
            }
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
                Text(model.label(of: url)).font(.system(.callout, design: .monospaced))
            }
            .listStyle(.bordered)
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

    /// A list under its small heading, as the Publish sheet heads its sections.
    private func headed<Content: View>(_ heading: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(heading)
                .font(.subheadline)
                .fontWeight(.semibold)
            content()
        }
    }
}

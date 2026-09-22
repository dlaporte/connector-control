import AppKit
import SwiftUI
import UniformTypeIdentifiers
import ConnectorControlState

/// The Collections window: the collections in the left pane, the selected one's connectors in the
/// right, the toolbar and the action links that act on it, and the four sheets it puts in front of
/// itself. Layout, bindings and the native panels only; every rule and string is CollectionsModel's.
struct CollectionsWindowView: View {
    /// The scene id, which the popover's Manage Collections… opens.
    static let windowID = "collections"

    /// The lock leading a synced row, dimmed so it reads as a mark rather than as a control.
    private static let lockOpacity = 0.55
    /// What joins the selected collection's name to its detail line. The model leaves the name
    /// out — its line says what is true of the collection, not which one — so the window, whose
    /// sidebar may be narrower than the name, is where the two are put together.
    private static let nameSeparator = " · "
    /// Wide enough for the longest collection name the sidebar is likely to hold without taking
    /// width the rows need for a type column and three controls.
    private static let sidebarWidth: CGFloat = 200

    @StateObject private var model: CollectionsModel
    /// The popover's request and the banner both travel through AppState, so this window repaints
    /// on them as well as on the model's own changes — and `onChange` can watch the request.
    @ObservedObject private var state: AppState
    @Environment(\.openWindow) private var openWindow
    @State private var sheet: Sheet?
    /// The model's last refusal, kept here because only the view knows when it has been read:
    /// `lastError` is cleared by the next action that succeeds, never by an OK button.
    @State private var shownError: String?

    init(state: AppState, dialogs: Dialogs) {
        self.state = state
        _model = StateObject(wrappedValue: CollectionsModel(state: state, dialogs: dialogs))
    }

    /// Which sheet is up, holding the model it was opened with. The model is made once, when the
    /// sheet opens: it reads the store as it stands at that moment, and a sheet whose model were
    /// rebuilt on every repaint would lose the ticks and the typing done in it.
    private struct Sheet: Identifiable {
        /// The kind, and what it was opened for: a request arriving while another sheet is up has
        /// to be able to replace it, which only a different id does. Stored rather than computed,
        /// because `Identifiable.id` is nonisolated and the models it reads are not.
        let id: String
        let content: Content

        enum Content {
            case importFile(ImportModel)
            case review(ReviewModel)
            case publish(PublishModel)
            case export(PublishModel)
        }

        @MainActor static func importFile(_ model: ImportModel) -> Sheet {
            Sheet(id: "import\(model.path)", content: .importFile(model))
        }

        @MainActor static func review(_ model: ReviewModel) -> Sheet {
            Sheet(id: "review\(model.collection)", content: .review(model))
        }

        @MainActor static func publish(_ model: PublishModel) -> Sheet {
            Sheet(id: "publish\(model.collection)", content: .publish(model))
        }

        @MainActor static func export(_ model: PublishModel) -> Sheet {
            Sheet(id: "export\(model.collection)", content: .export(model))
        }
    }

    var body: some View {
        NavigationSplitView {
            sidebar
        } detail: {
            detail
        }
        .frame(minWidth: 720, minHeight: 480)
        .toolbar { toolbar }
        .sheet(item: $sheet) { present($0) }
        .alert(Text(shownError ?? ""), isPresented: errorShowing) { }
        .onChange(of: model.lastError) { _, error in shownError = error }
        // On appear and on every change, because WindowGroup keeps this window and brings it
        // forward: one that read the request only on appear would strand every later one.
        .onAppear { consumeRequest() }
        .onChange(of: state.collectionsWindowRequest) { _, _ in consumeRequest() }
    }

    // MARK: - Sidebar

    private var sidebar: some View {
        List(model.items, selection: $model.selected) { item in
            HStack(spacing: 6) {
                Text(item.name)
                    .fontWeight(item.isActive ? .bold : .regular)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    // A collection whose document this machine has never found is still usable,
                    // and still shows its connectors; the dimmed name says it is not in step.
                    .foregroundStyle(item.isLocated ? .primary : .secondary)
                if item.kind == .synced {
                    Image(systemName: "link")
                        .imageScale(.small)
                        .foregroundStyle(.secondary)
                }
                if item.hasPendingUpdate {
                    Circle()
                        .fill(.orange)
                        .frame(width: 6, height: 6)
                }
                Spacer(minLength: 0)
            }
            // Selecting a collection only shows it. Making it the active one is a double-click,
            // which is what the window has to offer: every labelled route to it — the chip menu's
            // items — belongs to the popover, and this window owns no wording of its own.
            .onTapGesture(count: 2) { model.switchTo(item.name) }
        }
        .navigationSplitViewColumnWidth(min: 160, ideal: CollectionsWindowView.sidebarWidth)
    }

    // MARK: - Detail

    private var detail: some View {
        VStack(alignment: .leading, spacing: 8) {
            detailLine
            if let text = model.bannerText { banner(text) }
            rows
            actionLinks
        }
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    /// The selected collection, bold, then what the model says about it. Paths in that line are
    /// raw, so a window too narrow for one elides its middle rather than losing its end.
    private var detailLine: some View {
        (Text(model.selected ?? "").fontWeight(.semibold)
            + Text(CollectionsWindowView.nameSeparator + model.detailLine))
            .font(.callout)
            .foregroundStyle(.secondary)
            .lineLimit(1)
            .truncationMode(.middle)
    }

    /// One strip, one button: Stop Publishing is an action link under the rows already, which is
    /// the second button the popover's failed-publish banner needs and this one does not.
    @ViewBuilder private func banner(_ text: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Text(text)
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
            if let button = model.bannerButton {
                Button(button) { runBannerAction() }
            }
        }
        .padding(8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 6).fill(Color.orange.opacity(0.12)))
    }

    private var rows: some View {
        List(model.rows) { row in
            HStack(spacing: 8) {
                // One leading slot for both marks, so ticking a row cannot shift its name and a
                // synced collection's rows line up with a local one's.
                ZStack(alignment: .leading) {
                    if row.isLocked {
                        Image(systemName: "lock.fill")
                            .imageScale(.small)
                            .foregroundStyle(.secondary)
                            .opacity(CollectionsWindowView.lockOpacity)
                    } else {
                        Toggle("", isOn: checkedBinding(row))
                            .toggleStyle(.checkbox)
                            .labelsHidden()
                            .accessibilityLabel(row.name)
                    }
                }
                .frame(width: 18, alignment: .leading)
                Text(row.name)
                    .fontWeight(.medium)
                    .lineLimit(1)
                    .layoutPriority(1)
                if let caution = row.caution {
                    // Advisory only: the switch and the pencil stay live, and the tooltip sends
                    // the user to the editor's full note.
                    Image(systemName: PopoverModel.toolWarningGlyph)
                        .imageScale(.small)
                        .foregroundStyle(.orange)
                        .help(caution)
                        .accessibilityLabel(caution)
                }
                Spacer(minLength: 8)
                Text(row.typeText)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                Toggle("", isOn: enabledBinding(row))
                    .toggleStyle(.switch)
                    .controlSize(.small)
                    .labelsHidden()
                    .accessibilityLabel(row.name)
                Button {
                    openWindow(id: EditTarget.editorWindowID, value: model.editTarget(for: row.name))
                } label: {
                    Image(systemName: "pencil")
                        .imageScale(.medium)
                        .foregroundStyle(.secondary)
                }
                .buttonStyle(.accessoryBar)
                .accessibilityLabel(row.name)
            }
            .padding(.vertical, 2)
        }
        // The rows are the tall half of the window: the list takes what is left after the detail
        // line, the banner and the links, and scrolls inside it.
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    /// What can be done to the collection itself, rather than to one of its connectors. Each link
    /// is here only while its flag says the collection can take it.
    private var actionLinks: some View {
        HStack(spacing: 14) {
            Button(CollectionsModel.renameAction) { model.rename() }
            Button(CollectionsModel.deleteAction) { model.delete() }
                .disabled(!model.canDelete)
            // Two independent flags, not two halves of one: a collection whose document another
            // machine publishes can be published from this one as well, and both links belong to
            // it — the toolbar's Publish… follows the same flag.
            if model.canPublish {
                Button(CollectionsModel.publishButton) { sheet = .publish(publishModel()) }
            }
            if model.canStopPublishing {
                Button(CollectionsModel.stopPublishingAction) { model.stopPublishing() }
            }
            if model.canStopSyncing {
                Button(CollectionsModel.stopSyncingAction) { model.stopSyncing() }
            }
            Spacer(minLength: 0)
        }
        .buttonStyle(.link)
        .font(.callout)
    }

    // MARK: - Toolbar

    @ToolbarContentBuilder private var toolbar: some ToolbarContent {
        ToolbarItemGroup {
            Button(CollectionsModel.importButton) { openDocument(keepInSync: false) }
            Button(CollectionsModel.subscribeButton) { openDocument(keepInSync: true) }
            Button(CollectionsModel.exportButton(model.checkedNames.count)) { sheet = .export(publishModel()) }
                .disabled(!model.canExport)
            Button(CollectionsModel.publishButton) { sheet = .publish(publishModel()) }
                .disabled(!model.canPublish)
            Button(CollectionsModel.refreshButton) { model.refresh() }
                .disabled(!model.canRefresh)
            Button(CollectionsModel.makeLocalCopyButton) { model.makeLocalCopy() }
                .disabled(!model.canMakeLocalCopy)
        }
        ToolbarItem(placement: .primaryAction) {
            Button(CollectionsModel.newButton) { model.create() }
        }
    }

    // MARK: - Sheets

    @ViewBuilder private func present(_ sheet: Sheet) -> some View {
        switch sheet.content {
        case .importFile(let model):
            ImportSheetView(model: model) { self.sheet = nil }
        case .review(let model):
            ReviewSheetView(model: model) { self.sheet = nil }
        case .publish(let model):
            PublishSheetView(model: model, mode: .publish) { self.sheet = nil }
        case .export(let model):
            PublishSheetView(model: model, mode: .export) { self.sheet = nil }
        }
    }

    private func importModel(path: String, keepInSync: Bool) -> ImportModel {
        let sheetModel = ImportModel(state: state, path: path)
        // Subscribe… is Import… with the second mode already chosen: the panel that opened it
        // said which of the two the user asked for.
        if keepInSync { sheetModel.mode = .keepInSync }
        return sheetModel
    }

    private func publishModel() -> PublishModel {
        PublishModel(state: state, collection: model.selected ?? "")
    }

    // MARK: - Requests

    /// What the popover asked for, taken so no other window can act on it twice.
    private func consumeRequest() {
        switch state.takeCollectionsWindowRequest() {
        case .importFile:
            openDocument(keepInSync: false)
        case .exportActive:
            // The menu item names the collection that is active now, not whichever one this
            // window last had selected.
            model.selected = state.activeCollection
            sheet = .export(publishModel())
        case .review(let collection):
            model.selected = collection
            sheet = .review(ReviewModel(state: state, collection: collection))
        case nil:
            break
        }
    }

    /// The banner's one button. True says the news needs nothing from the file system and the
    /// Review sheet is the whole answer; false says this view owes a panel, and which one is what
    /// the banner is — the model then refuses anything that is not what it asked for.
    private func runBannerAction() {
        if model.bannerAction() {
            if let collection = model.selected {
                sheet = .review(ReviewModel(state: state, collection: collection))
            }
            return
        }
        switch state.collectionBanner {
        case .locate:
            if let path = chooseDocument() { shownError = model.locateSource(path) }
        case .publishFailed:
            if let folder = chooseFolder() { shownError = model.choosePublishFolder(folder) }
        case .updateAvailable, nil:
            break
        }
    }

    // MARK: - Panels

    /// Import… and Subscribe… are the same panel: one document, and what happens to it is the
    /// sheet's question rather than the picker's.
    private func openDocument(keepInSync: Bool) {
        guard let path = chooseDocument() else { return }
        sheet = .importFile(importModel(path: path, keepInSync: keepInSync))
    }

    private func chooseDocument() -> String? {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        panel.allowsMultipleSelection = false
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK else { return nil }
        return panel.url?.path
    }

    /// Settings ▸ Storage's panel, for the folder a failed publish is asking to be pointed at.
    private func chooseFolder() -> String? {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = "Choose"
        guard panel.runModal() == .OK else { return nil }
        return panel.url?.path
    }

    // MARK: - Bindings

    private func checkedBinding(_ row: CollectionsModel.Row) -> Binding<Bool> {
        Binding(get: { row.checked }, set: { model.setChecked(row.name, $0) })
    }

    private func enabledBinding(_ row: CollectionsModel.Row) -> Binding<Bool> {
        Binding(get: { row.enabled }, set: { model.setEnabled(row.name, $0) })
    }

    /// The alert's own switch. Dismissing it drops the copy this view holds; the model's
    /// `lastError` is not the view's to clear.
    private var errorShowing: Binding<Bool> {
        Binding(get: { shownError != nil }, set: { if !$0 { shownError = nil } })
    }
}

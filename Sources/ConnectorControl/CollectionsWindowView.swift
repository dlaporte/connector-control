import AppKit
import SwiftUI
import ConnectorControlState

/// The Collections window: the collections in the left pane, the selected one's connectors in the
/// right, the header menu and the selection bar that act on them, and the five sheets it puts in
/// front of itself. Layout, bindings and the native panels only; every rule and string is
/// CollectionsModel's.
struct CollectionsWindowView: View {
    /// The scene id, which the popover's Manage Collections opens.
    static let windowID = "collections"

    /// The lock leading a synced row, dimmed so it reads as a mark rather than as a control.
    private static let lockOpacity = 0.55
    /// Wide enough for the longest collection name the sidebar is likely to hold without taking
    /// width the rows need for a target column and the pencil.
    private static let sidebarWidth: CGFloat = 200
    /// The most the name column takes, however long the longest name: past it the name is cut and
    /// the target keeps the room it needs to say anything.
    private static let maxNameWidth: CGFloat = 240
    /// The selection bar's height, the same idle or ticked, so ticking a row cannot move the list.
    private static let selectionBarHeight: CGFloat = 30

    @StateObject private var model: CollectionsModel
    /// The popover's request travels through AppState, not the model, so this window observes
    /// AppState too, for `onChange` to see the request arrive. Everything else it shows, the
    /// banner included, is the model's.
    @ObservedObject private var state: AppState
    @Environment(\.openWindow) private var openWindow
    @State private var sheet: Sheet?
    /// A sheet asked for while another is still on screen. Dismissing one is animated, and the
    /// replacement can only go up once that has finished — which is what `onDismiss` reports.
    @State private var pendingSheet: Sheet?
    /// Whether the window is on screen. The request is taken a turn after it is noticed, and the
    /// window can close in that turn: a request taken then would be lost to a window nobody sees,
    /// and an import would put a file panel up with no window behind it. The Windows mirror
    /// guards the same moment with its `closed` flag.
    @State private var appeared = false
    /// The model's last refusal, kept here because only the view knows when it has been read:
    /// `lastError` is cleared by the next action that succeeds, never by an OK button.
    @State private var shownError: String?
    /// The longest name cell in the list, so every target starts at the same x.
    @State private var nameWidth: CGFloat = 0

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
            case copy(CopyModel)
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

        @MainActor static func copy(_ model: CopyModel) -> Sheet {
            Sheet(id: "copy\(model.destination)", content: .copy(model))
        }
    }

    /// The widest name cell, gathered from every row rather than only the ones on screen.
    private struct NameWidthKey: PreferenceKey {
        static let defaultValue: CGFloat = 0
        static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
            value = max(value, nextValue())
        }
    }

    var body: some View {
        NavigationSplitView {
            sidebar
        } detail: {
            detail
        }
        .frame(minWidth: 720, minHeight: 480)
        .sheet(item: $sheet, onDismiss: { presentPending() }) { present($0) }
        // Attached to this window rather than said through Dialogs.inform, which is app-modal:
        // a refusal here is about this window, and a window-modal alert is the Mac's way to say so.
        .alert(Text(shownError ?? ""), isPresented: errorShowing) { }
        // On appear and on every change, because the window stays open and is brought forward:
        // one that read the request only on appear would strand every later one. Both go through
        // the next turn of the main queue, so the panel or sheet a request opens appears over a
        // window that is already on screen and frontmost rather than inside its first layout.
        .onAppear {
            appeared = true
            scheduleRequest()
        }
        .onDisappear { appeared = false }
        .onChange(of: state.collectionsWindowRequest) { _, _ in scheduleRequest() }
    }

    // MARK: - Sidebar

    private var sidebar: some View {
        // The header sits above the list rather than in a section of it: a sidebar section grows
        // a collapse chevron on hover, in the very corner the `+` occupies.
        VStack(alignment: .leading, spacing: 0) {
            sidebarHeader
                .padding(.leading, 18)
                .padding(.trailing, 12)
                .padding(.vertical, 4)
            List(model.items, selection: $model.selected) { item in
                sidebarRow(item)
            }
        }
        .navigationSplitViewColumnWidth(min: 160, ideal: CollectionsWindowView.sidebarWidth)
    }

    /// The section title and the `+` whose menu makes a collection or brings one in. Import and
    /// Subscribe carry their subtitles, because which of the two to use is the one question the
    /// words alone do not answer. A menu row draws its subtitle only from macOS 14.4, so each
    /// also carries it as help.
    private var sidebarHeader: some View {
        HStack {
            Text(CollectionsModel.windowTitle)
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(.secondary)
            Spacer(minLength: 0)
            Menu {
                Button(CollectionsModel.newButton) { act { model.create() } }
                Divider()
                Button { openDocument(keepInSync: false) } label: {
                    Text(CollectionsModel.importButton)
                    Text(CollectionsModel.importSubtitle)
                }
                .help(CollectionsModel.importSubtitle)
                Button { openDocument(keepInSync: true) } label: {
                    Text(CollectionsModel.subscribeButton)
                    Text(CollectionsModel.subscribeSubtitle)
                }
                .help(CollectionsModel.subscribeSubtitle)
            } label: {
                Image(systemName: "plus")
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            .help(CollectionsModel.addCollectionTooltip)
            .accessibilityLabel(CollectionsModel.addCollectionTooltip)
        }
    }

    private func sidebarRow(_ item: CollectionsModel.Item) -> some View {
        HStack(spacing: 6) {
            Text(item.name)
                .fontWeight(item.isActive ? .bold : .regular)
                .lineLimit(1)
                .truncationMode(.middle)
                // A collection whose document this machine has never found is still usable,
                // and still shows its connectors; the dimmed name says it is not in step.
                .foregroundStyle(item.isLocated ? .primary : .secondary)
            // The marks sit at the trailing edge, where the mockup and the Windows sidebar
            // put them, rather than trailing the name at whatever width it happens to be.
            Spacer(minLength: 6)
            if item.kind == .synced { chain(item) }
            if item.hasPendingUpdate {
                Circle()
                    .fill(.orange)
                    .frame(width: 6, height: 6)
            }
        }
        // The row answers across its whole width, not only over its text.
        .contentShape(Rectangle())
        // Selecting a collection only shows it; making it the active one is a second action.
        // The double-click is the quick one — simultaneous, so it does not swallow the single
        // click that selects — and the context menu is the labelled route that a keyboard
        // reaches and a screen reader reads out.
        .simultaneousGesture(TapGesture(count: 2).onEnded { act { model.switchTo(item.name) } })
        .contextMenu {
            Button(CollectionsModel.makeActiveAction) { act { model.switchTo(item.name) } }
                .disabled(item.isActive)
        }
    }

    /// The chain, and the sentence naming the document behind it — which the model withholds for
    /// a synced collection whose file it cannot name. An empty tooltip and a nameless label are
    /// worse than neither, so the glyph goes up bare instead.
    @ViewBuilder private func chain(_ item: CollectionsModel.Item) -> some View {
        let glyph = Image(systemName: "link")
            .imageScale(.small)
            .foregroundStyle(Color.secondary)
        if let tooltip = CollectionsModel.syncedGlyphTooltip(item) {
            glyph.help(tooltip).accessibilityLabel(tooltip)
        } else {
            glyph
        }
    }

    // MARK: - Detail

    private var detail: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 8) {
                header
                if let text = model.bannerText { banner(text) }
                connectorsHeader
                rows
            }
            .padding([.horizontal, .top], 12)
            .padding(.bottom, 8)
            selectionBar
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    /// The collection's name, the pills that mark it as other than an ordinary local one, and the
    /// `⋯` that holds everything done to the collection itself. Nothing else goes in this line.
    private var header: some View {
        HStack(spacing: 8) {
            Text(model.selected ?? "")
                .font(.title3.weight(.semibold))
                .lineLimit(1)
                .truncationMode(.middle)
            ForEach(model.pills, id: \.self) { pill in
                pillView(pill)
            }
            Spacer(minLength: 8)
            Menu {
                collectionMenu
            } label: {
                Image(systemName: "ellipsis.circle")
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            .help(CollectionsModel.moreActionsLabel)
            .accessibilityLabel(CollectionsModel.moreActionsLabel)
        }
    }

    /// Active is the one pill in colour: it is the collection Claude is running, and the others
    /// only say where a collection's document comes from or goes.
    private func pillView(_ pill: CollectionsModel.Pill) -> some View {
        let tint: Color = pill == .active ? .green : .secondary
        return Text(CollectionsModel.title(for: pill))
            .font(.caption)
            .foregroundStyle(tint)
            .padding(.horizontal, 7)
            .padding(.vertical, 1)
            .overlay(Capsule().stroke(tint))
    }

    /// The model's list, in its order; the entries that do not apply are already left out, and
    /// the two it dims arrive saying so.
    @ViewBuilder private var collectionMenu: some View {
        // By position: the separators repeat, so the entries cannot identify themselves.
        ForEach(Array(model.collectionMenu.enumerated()), id: \.offset) { _, entry in
            if entry == .separator {
                Divider()
            } else {
                Button(CollectionsModel.title(for: entry)) { run(entry) }
                    .disabled(!CollectionsModel.isEnabled(entry))
            }
        }
    }

    /// Every entry, as the Windows window's Run takes them. Both publish entries open the same
    /// sheet: a published collection's is its settings. Export All writes the whole collection,
    /// so its sheet takes no subset.
    private func run(_ entry: CollectionsModel.MenuEntry) {
        switch entry {
        case .makeActive:
            if let collection = model.selected { act { model.switchTo(collection) } }
        case .rename:
            act { model.rename() }
        case .duplicate:
            act { model.duplicate() }
        case .startPublishing, .publishingSettings:
            if let collection = model.selected { show(.publish(publishModel(for: collection))) }
        case .exportAll:
            if let collection = model.selected { show(.export(publishModel(for: collection))) }
        case .delete:
            act { model.delete() }
        case .stopPublishing:
            act { model.stopPublishing() }
        case .showPublishedFile:
            reveal(model.publishedFilePath)
        case .showSourceFile:
            reveal(model.sourceFilePath)
        case .makeLocalCopy:
            act { model.makeLocalCopy() }
        case .refresh:
            act { model.refresh() }
        case .stopSyncing:
            act { model.stopSyncing() }
        case .separator:
            break
        }
    }

    private func reveal(_ path: String?) {
        guard let path else { return }
        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
    }

    /// One strip, one button: Stop Publishing is in the header's menu already, which is the
    /// second button the popover's failed-publish banner needs and this one does not.
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

    /// The list's title and count, and the `+` that adds a connector here — dimmed on a synced
    /// collection, whose tooltip then says where additions go instead.
    private var connectorsHeader: some View {
        HStack(spacing: 6) {
            Text(CollectionsModel.connectorsHeader)
                .fontWeight(.semibold)
            Text("\(model.rows.count)")
                .foregroundStyle(.secondary)
            Spacer(minLength: 0)
            Button {
                openWindow(id: EditTarget.editorWindowID, value: model.newConnectorTarget())
            } label: {
                Image(systemName: "plus")
            }
            .buttonStyle(.accessoryBar)
            .disabled(!model.canAddConnector)
            .help(model.addConnectorTooltipText)
            .accessibilityLabel(model.addConnectorTooltipText)
        }
        .font(.callout)
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
                            .help(CollectionsModel.lockedGlyphTooltip)
                            .accessibilityLabel(CollectionsModel.lockedGlyphTooltip)
                    } else {
                        Toggle("", isOn: checkedBinding(row))
                            .toggleStyle(.checkbox)
                            .labelsHidden()
                            .accessibilityLabel(row.name)
                    }
                }
                .frame(width: 18, alignment: .leading)
                nameCell(row)
                    .frame(width: nameWidth, alignment: .leading)
                Text(row.target)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .frame(maxWidth: .infinity, alignment: .leading)
                Button {
                    openWindow(id: EditTarget.editorWindowID, value: model.editTarget(for: row.name))
                } label: {
                    Image(systemName: "pencil")
                        .imageScale(.medium)
                        .foregroundStyle(.secondary)
                }
                .buttonStyle(.accessoryBar)
                .help(CollectionsModel.editTooltip)
                .accessibilityLabel(CollectionsModel.editTooltip)
            }
            .padding(.vertical, 2)
        }
        // The rows are the tall half of the window: the list takes what is left after the header,
        // the banner and the list's own title, and scrolls inside it.
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        // Every row's name cell measured at its natural width, off screen rows included, since
        // the list lays out only the rows it shows.
        .background(alignment: .topLeading) {
            ZStack(alignment: .topLeading) {
                ForEach(model.rows) { row in
                    nameCell(row)
                        .fixedSize()
                        .background(GeometryReader { proxy in
                            Color.clear.preference(key: NameWidthKey.self, value: proxy.size.width)
                        })
                }
            }
            .hidden()
        }
        .onPreferenceChange(NameWidthKey.self) { width in
            nameWidth = min(width, CollectionsWindowView.maxNameWidth)
        }
    }

    /// The name and its caution mark, measured and laid out as one: the column is as wide as the
    /// longest of these, so a mark cannot push its row's target out of line.
    @ViewBuilder private func nameCell(_ row: CollectionsModel.Row) -> some View {
        HStack(spacing: 4) {
            Text(row.name)
                .fontWeight(.medium)
                .lineLimit(1)
            if let caution = row.caution {
                // Advisory only: the pencil stays live, and the tooltip sends the user to the
                // editor's full note.
                Image(systemName: PopoverModel.toolWarningGlyph)
                    .imageScale(.small)
                    .foregroundStyle(.orange)
                    .help(caution)
                    .accessibilityLabel(caution)
            }
        }
    }

    // MARK: - Selection bar

    /// Idle, what the collection is and where its document lives; with rows ticked, what can be
    /// done to them. Remove sits apart at the far end, so a hand moving from the safe pair cannot
    /// land on it, and is absent where the rows are not the user's to remove.
    private var selectionBar: some View {
        VStack(spacing: 0) {
            Divider()
            HStack(spacing: 10) {
                if model.checkedNames.isEmpty {
                    // Paths in this line are raw, so a window too narrow for one elides its middle
                    // rather than losing its end.
                    Text(model.detailLine)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Spacer(minLength: 0)
                } else {
                    Text(CollectionsModel.selectedCount(model.checkedNames.count))
                        .fontWeight(.semibold)
                    Menu(CollectionsModel.copyToButton) {
                        ForEach(model.copyDestinations) { destination in
                            Button { copy(to: destination.name) } label: {
                                Text(destination.name)
                                if !destination.isEnabled { Text(CollectionsModel.readOnlyNote) }
                            }
                            .disabled(!destination.isEnabled)
                            .help(destination.isEnabled ? "" : CollectionsModel.readOnlyNote)
                        }
                        if !model.copyDestinations.isEmpty { Divider() }
                        Button(CollectionsModel.newButton) { act { model.copyCheckedIntoNewCollection() } }
                    }
                    .fixedSize()
                    Button(CollectionsModel.exportCheckedButton) { show(.export(exportModel())) }
                    Spacer(minLength: 16)
                    if model.canRemoveChecked {
                        Button(CollectionsModel.removeCheckedButton) { act { model.removeChecked() } }
                    }
                }
            }
            .padding(.horizontal, 12)
            .frame(height: CollectionsWindowView.selectionBarHeight)
        }
    }

    /// Straight through when nothing clashes; otherwise the sheet asks about the clashes first.
    private func copy(to destination: String) {
        if model.checkedNamesClashing(in: destination).isEmpty {
            act { _ = model.copyChecked(into: destination) }
        } else {
            show(.copy(CopyModel(collections: model, destination: destination)))
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
        case .copy(let copyModel):
            CopySheetView(model: copyModel, refusal: { model.lastError }) { self.sheet = nil }
        }
    }

    private func importModel(path: String, keepInSync: Bool) -> ImportModel {
        let sheetModel = ImportModel(state: state, path: path)
        // Subscribe is Import with the second mode already chosen: the panel that opened it
        // said which of the two the user asked for.
        if keepInSync { sheetModel.mode = .keepInSync }
        return sheetModel
    }

    /// The Publish sheet's model, which is the Export All sheet's too: both take the whole
    /// collection.
    private func publishModel(for collection: String) -> PublishModel {
        PublishModel(state: state, collection: collection)
    }

    /// The Export sheet writes only what is ticked, which is what the selection bar's count says.
    /// The Publish sheet above takes no subset: publishing binds the whole collection.
    private func exportModel() -> PublishModel {
        PublishModel(state: state, collection: model.selected ?? "",
                     connectors: model.exportIntentForChecked())
    }

    // MARK: - Requests

    /// The next turn of the main queue, so a request that arrives with the window — `onAppear`
    /// runs inside its first layout — does not put a modal panel in front of a window that is
    /// not on screen yet.
    private func scheduleRequest() {
        Task { @MainActor in consumeRequest() }
    }

    /// Puts a sheet up, replacing whatever is already there. SwiftUI will not swap one item for
    /// another: the open sheet has to be dismissed first, and the new one presented after it has
    /// gone — not one turn later, which is shorter than the dismissal itself.
    private func show(_ next: Sheet) {
        guard sheet != nil else { sheet = next; return }
        pendingSheet = next
        sheet = nil
    }

    /// Whatever `show(_:)` had to put aside, now that the sheet in its way has gone. Nothing to
    /// do for the ordinary case, where a sheet was dismissed and nothing is waiting behind it.
    private func presentPending() {
        guard let next = pendingSheet else { return }
        pendingSheet = nil
        sheet = next
    }

    /// What the popover asked for, taken so no other window can act on it twice.
    private func consumeRequest() {
        // Left in AppState for whichever window opens next.
        guard appeared else { return }
        switch state.takeCollectionsWindowRequest() {
        case .review(let collection):
            model.selected = collection
            show(.review(ReviewModel(state: state, collection: collection)))
        case .publish(let collection):
            // A publish the app stopped for review: the sheet is where the author answers it.
            model.selected = collection
            show(.publish(publishModel(for: collection)))
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
                show(.review(ReviewModel(state: state, collection: collection)))
            }
            return
        }
        switch model.banner {
        case .locate:
            if let path = FilePanels.chooseCollectionDocument() { shownError = model.locateSource(path) }
        case .publishFailed:
            if let folder = FilePanels.chooseFolder() { shownError = model.choosePublishFolder(folder) }
        case .publishBlocked:
            // Stopped for review, not for a folder: choosing another folder would only move the
            // failure there, so the answer is the Publish sheet.
            if let collection = model.selected { show(.publish(publishModel(for: collection))) }
        case .updateAvailable, nil:
            break
        }
    }

    // MARK: - Panels

    /// Import and Subscribe are the same panel: one document, and what happens to it is the
    /// sheet's question rather than the picker's.
    private func openDocument(keepInSync: Bool) {
        guard let path = FilePanels.chooseCollectionDocument() else { return }
        show(.importFile(importModel(path: path, keepInSync: keepInSync)))
    }

    // MARK: - Bindings

    private func checkedBinding(_ row: CollectionsModel.Row) -> Binding<Bool> {
        Binding(get: { row.checked }, set: { model.setChecked(row.name, $0) })
    }

    /// One collection action and the refusal it may leave behind, read straight after the call
    /// rather than watched: `lastError` set twice to the same sentence is not a change, and a
    /// second identical refusal has to be shown too. The Windows mirror's `Act` does the same.
    private func act(_ action: () -> Void) {
        action()
        shownError = model.lastError
    }

    /// The alert's own switch. Dismissing it drops the copy this view holds; the model's
    /// `lastError` is not the view's to clear.
    private var errorShowing: Binding<Bool> {
        Binding(get: { shownError != nil }, set: { if !$0 { shownError = nil } })
    }
}

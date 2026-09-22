import AppKit
import SwiftUI
import UniformTypeIdentifiers
import ConnectorControlState

struct PopoverView: View {
    /// The chip's disclosure mark. A mark rather than wording, and the one piece of the chip
    /// `PopoverModel.collectionChipText` cannot hand over on its own: the chain and the dot go
    /// between the name and this, so the two halves of that string are needed apart.
    private static let disclosureMark = "▾"

    @StateObject private var model: PopoverModel
    @Environment(\.openWindow) private var openWindow
    @Environment(\.openSettings) private var openSettings
    /// A collection action's refusal, kept here because only the view knows when it has been
    /// read: the model returns it rather than publishing it, and no OK button reaches back.
    @State private var shownError: String?

    init(state: AppState) {
        _model = StateObject(wrappedValue: PopoverModel(state: state))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider()
            if let error = model.errorMessage { errorBanner(error) }
            if let text = model.collectionBannerText { collectionBanner(text) }
            mcpList
            if model.showFooter {
                Divider()
                footer
            }
        }
        .frame(minWidth: 240, maxWidth: 380)
        .background(WindowAutoSizer())
        .alert(Text(shownError ?? ""), isPresented: errorShowing) { }
        .onAppear { model.opened() }
    }

    private func openEditor(_ target: EditTarget) {
        openWindow(id: EditTarget.editorWindowID, value: target)
        NSApp.activate(ignoringOtherApps: true)
    }

    private var header: some View {
        HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 1) {
                Text(PopoverModel.title).font(.headline)
                Text(model.subtitle).font(.caption2).foregroundStyle(.secondary)
                collectionChip
            }
            Spacer(minLength: 20)
            HStack(spacing: 0) {
                Button {
                    openEditor(.newRemote())
                } label: {
                    headerIcon("plus")
                }
                .buttonStyle(.accessoryBar)
                // A synced collection's connectors are the author's; the tooltip is the model's
                // to pick, because it says why the button is dead as often as what it does.
                .disabled(!model.canAddConnector)
                .help(model.addTooltipText)
                Button {
                    NSApp.activate(ignoringOtherApps: true)
                    openSettings()
                } label: {
                    headerIcon("gearshape")
                }
                .buttonStyle(.accessoryBar)
                .help(PopoverModel.settingsTooltip)
                Button {
                    model.quit()
                } label: {
                    headerIcon("power")
                }
                .buttonStyle(.accessoryBar)
                .help(PopoverModel.quitTooltip)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.quinary)
    }

    /// The active collection and what is true of it: the chain when its connectors are a
    /// document's, and the amber dot when that document has news waiting.
    private var collectionChip: some View {
        Menu {
            collectionMenu
        } label: {
            HStack(spacing: 4) {
                Text(activeCollectionName)
                if model.activeCollectionIsSynced {
                    Image(systemName: "link")
                        .imageScale(.small)
                        .help(model.sourceTooltip ?? "")
                }
                if model.activeHasPendingUpdate { pendingDot }
                Text(PopoverView.disclosureMark)
            }
            .font(.caption2.weight(.semibold))
            .foregroundStyle(.secondary)
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .padding(.top, 1)
    }

    /// The active collection's name. The model's chip text bakes the disclosure mark into the
    /// same string, and the chain and the dot belong between the two, so the name is read off
    /// the menu item that carries the check instead.
    private var activeCollectionName: String {
        model.collectionItems.first(where: \.isActive)?.name ?? ""
    }

    /// An update is waiting at the source of the collection this sits beside.
    private var pendingDot: some View {
        Circle().fill(.orange).frame(width: 6, height: 6)
    }

    /// The collections to switch between, then the two document commands, then the window that
    /// owns everything else — creating, renaming and deleting included.
    @ViewBuilder private var collectionMenu: some View {
        ForEach(model.collectionItems) { item in
            Button {
                model.switchCollection(item.name)
            } label: {
                HStack(spacing: 4) {
                    if item.isActive { Image(systemName: "checkmark") }
                    Text(item.name)
                    if item.isSynced { Image(systemName: "link") }
                    if item.hasPendingUpdate { pendingDot }
                }
            }
        }
        Divider()
        Button(PopoverModel.importTitle) { openCollections { model.requestImport() } }
        // A synced collection is the author's document already; passing a second copy of it on
        // is theirs to do, not this machine's.
        if !model.activeCollectionIsSynced {
            Button(model.exportTitle) { openCollections { model.requestExport() } }
        }
        Divider()
        Button(PopoverModel.manageTitle) { openCollections() }
    }

    private func errorBanner(_ message: String) -> some View {
        Label(message, systemImage: "xmark.octagon.fill")
            .font(.callout)
            .foregroundStyle(.red)
            .fixedSize(horizontal: false, vertical: true)
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// The one collection with news, active or not. Its buttons take a line of their own rather
    /// than a column beside the sentence: the popover is 240–380 points wide, and a failed
    /// publish offers two of them.
    private func collectionBanner(_ text: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Label(text, systemImage: PopoverModel.toolWarningGlyph)
                .font(.caption)
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 8) {
                Spacer(minLength: 0)
                if let button = model.collectionBannerButton {
                    Button(button) { runBannerAction() }
                }
                if let secondary = model.collectionBannerSecondaryButton {
                    Button(secondary) { model.collectionBannerSecondaryAction() }
                        .buttonStyle(.link)
                }
            }
            .controlSize(.small)
        }
        .padding(8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.orange.opacity(0.12))
    }

    /// The banner's first button. True says the news needs nothing from the file system and the
    /// Collections window is the whole answer; false says this view owes a panel, and which one
    /// is what the banner is — the model then refuses anything that is not what it asked for.
    private func runBannerAction() {
        if model.collectionBannerAction() {
            openCollections()
            return
        }
        switch model.collectionBanner {
        case .locate:
            if let path = chooseDocument() { shownError = model.locateSource(path) }
        case .publishFailed:
            if let folder = chooseFolder() { shownError = model.choosePublishFolder(folder) }
        case .updateAvailable, nil:
            break
        }
    }

    /// The Collections window, which takes no arguments: whatever the popover wants in front of
    /// it is set as a request first, because the window reads that as it appears.
    private func openCollections(_ request: () -> Void = {}) {
        request()
        openWindow(id: CollectionsWindowView.windowID)
        NSApp.activate(ignoringOtherApps: true)
    }

    /// The Collections window's panel, for the document a collection is asking to be pointed at.
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

    /// The alert's own switch. Dismissing it drops the copy this view holds; there is nothing
    /// behind it to clear, because the refusal was handed back rather than published.
    private var errorShowing: Binding<Bool> {
        Binding(get: { shownError != nil }, set: { if !$0 { shownError = nil } })
    }

    /// Cap before the list scrolls (~12 rows); large catalogs stay usable
    /// without the popover outgrowing the screen.
    private static let maxListHeight: CGFloat = 420
    @State private var listContentHeight: CGFloat = 0

    private var mcpList: some View {
        // A bare ScrollView collapses here: the MenuBarExtra window sizes to
        // the content's IDEAL height and a ScrollView's ideal is zero. So the
        // list's natural height is measured and the ScrollView gets an
        // explicit frame — growing with content up to the cap, scrolling past.
        ScrollView {
            VStack(spacing: 0) {
                ForEach(model.rows) { row in
                    MCPRow(row: row,
                           onToggle: { model.setEnabled(row.name, $0) },
                           onEdit: {
                               if let entry = model.entryFor(row.name) {
                                   openEditor(.existing(name: row.name, entry: entry))
                               }
                           })
                    Divider()
                }
                if model.isEmpty {
                    Text(PopoverModel.emptyText)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .padding()
                }
            }
            .onGeometryChange(for: CGFloat.self) { proxy in
                proxy.size.height
            } action: { height in
                listContentHeight = height
            }
        }
        .frame(height: min(max(listContentHeight, 1), Self.maxListHeight))
    }

    private var footer: some View {
        HStack {
            Spacer()
            Button {
                model.footerAction()
            } label: {
                Label(model.footerTitle, systemImage: model.footerGlyph)
            }
            .buttonStyle(.borderedProminent)
            .tint(model.footer == .retryApply ? .red : .orange)
            .controlSize(.small)
        }
        .padding(10)
    }

    private func headerIcon(_ systemName: String) -> some View {
        // resizable + scaledToFit centers by geometric bounds; centering by
        // font metrics leaves different glyphs (plus vs gear) at different
        // heights because SF Symbols align on the text baseline.
        Image(systemName: systemName)
            .resizable()
            .scaledToFit()
            .fontWeight(.medium)
            .foregroundStyle(.secondary)
            .frame(width: 12, height: 12)
            .frame(width: 17, height: 17)
    }
}

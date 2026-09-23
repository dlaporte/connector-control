import AppKit
import SwiftUI
import UniformTypeIdentifiers
import ConnectorControlState

struct PopoverView: View {
    /// The chip's disclosure mark, drawn after the chain and the dot. A mark rather than wording,
    /// like the chain and the dot themselves: `.borderlessButton` menus draw no chevron of their own.
    private static let disclosureMark = "▾"

    @StateObject private var model: PopoverModel
    @Environment(\.openWindow) private var openWindow
    @Environment(\.openSettings) private var openSettings

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
        .onAppear { model.opened() }
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
                Text(model.activeCollection)
                if model.activeCollectionIsSynced {
                    let chain = Image(systemName: "link")
                        .imageScale(.small)
                        .help(model.sourceTooltip ?? "")
                    // A glyph on its own reads as nothing; it speaks its tooltip when the model
                    // has a source to name, and keeps SF Symbols' own label when it has none.
                    if let source = model.sourceTooltip {
                        chain.accessibilityLabel(source)
                    } else {
                        chain
                    }
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

    /// An update is waiting at the source of the collection this sits beside.
    private var pendingDot: some View {
        Circle().fill(.orange).frame(width: 6, height: 6)
            .help(PopoverModel.pendingSpokenLabel)
            .accessibilityLabel(PopoverModel.pendingSpokenLabel)
    }

    /// The collections to switch between, then the window that owns everything else —
    /// creating, renaming, deleting, and the document commands included.
    @ViewBuilder private var collectionMenu: some View {
        ForEach(model.collectionItems) { item in
            // A macOS menu row is one title and one image. The check is the item's state rather
            // than an image — which is what a Toggle in a menu becomes — so the image is free for
            // the chain, and a pending update is words in the model's title instead of a dot.
            Toggle(isOn: activeBinding(item)) {
                if item.isSynced {
                    Label(PopoverModel.menuTitle(for: item), systemImage: "link")
                } else {
                    Text(PopoverModel.menuTitle(for: item))
                }
            }
            .help(PopoverModel.menuTooltip(for: item) ?? "")
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
            Label(text, systemImage: PopoverModel.cautionGlyph)
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
            if let path = chooseDocument() { tell(model.locateSource(path)) }
        case .publishFailed:
            if let folder = chooseFolder() { tell(model.choosePublishFolder(folder)) }
        // A blocked publish never reaches here: its action queues the Publish sheet and reports
        // true, which opens the window above.
        case .updateAvailable, .publishBlocked, nil:
            break
        }
    }

    /// A refusal, said app-modally. A panel takes key focus from the popover on its way in, and
    /// the popover closes with it, so an alert attached to this view would have nothing left to
    /// present it by the time there is anything to say.
    private func tell(_ failure: String?) {
        guard let failure else { return }
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = failure
        alert.addButton(withTitle: AlertDialogs.okTitle)
        alert.runModal()
    }

    /// A menu row's check. Choosing an unchecked row makes that collection the active one;
    /// choosing the checked row asks to turn it off, which means nothing for a collection, so it
    /// is left alone. The check is read back from the model rather than kept here.
    private func activeBinding(_ item: CollectionMenuItem) -> Binding<Bool> {
        Binding(get: { item.isActive }, set: { on in if on { model.switchCollection(item.name) } })
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
                    MCPRow(row: row, onToggle: { model.setEnabled(row.name, $0) })
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
        // font metrics leaves different glyphs (gear vs power) at different
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

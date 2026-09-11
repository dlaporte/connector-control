import SwiftUI
import ConnectorControlState

struct PopoverView: View {
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

    private func openEditor(_ target: EditTarget) {
        openWindow(id: "editor", value: target)
        NSApp.activate(ignoringOtherApps: true)
    }

    private var header: some View {
        HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 1) {
                Text(PopoverModel.title).font(.headline)
                Text(model.subtitle).font(.caption2).foregroundStyle(.secondary)
                profileChip
            }
            Spacer(minLength: 20)
            HStack(spacing: 0) {
                Button {
                    openEditor(.newRemote())
                } label: {
                    headerIcon("plus")
                }
                .buttonStyle(.accessoryBar)
                .help(PopoverModel.addTooltip)
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

    private var profileChip: some View {
        Menu {
            ForEach(model.profileItems) { item in
                Button {
                    model.switchProfile(item.name)
                } label: {
                    if item.isActive {
                        Label(item.name, systemImage: "checkmark")
                    } else {
                        Text(item.name)
                    }
                }
            }
            Divider()
            Button(PopoverModel.newProfileTitle) { model.newProfile() }
            Button(model.renameProfileTitle) { model.renameProfile() }
            Button(model.deleteProfileTitle) { model.deleteProfile() }
                .disabled(!model.canDeleteProfile)
        } label: {
            Text(model.profileChipText)
                .font(.caption2.weight(.semibold))
                .foregroundStyle(.secondary)
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .padding(.top, 1)
    }

    private func errorBanner(_ message: String) -> some View {
        Label(message, systemImage: "xmark.octagon.fill")
            .font(.callout)
            .foregroundStyle(.red)
            .fixedSize(horizontal: false, vertical: true)
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
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

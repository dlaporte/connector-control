import SwiftUI
import ConnectorControlCore
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

/// MenuBarExtra's window grows with its content but doesn't reliably SHRINK
/// when content gets shorter (footer clearing, banner dismissing, row removal),
/// leaving dead space above and below the vertically-centered content. This
/// shim snaps the window frame to the content's fitted height, hung from a
/// recorded top-edge anchor.
///
/// The sizing must be IDEMPOTENT: three resize passes race here (SwiftUI's
/// auto-grow, this shim, the measured scroll-cap feedback), and a violent
/// content change like a profile switch interleaves them. A delta-based
/// correction that preserves the current top edge perpetuates whatever
/// transient frame it happened to read — the frame is therefore always set
/// to absolutes (anchored top, content-ideal height) computed from stable
/// facts, so competing passes converge instead of compounding drift.
private struct WindowAutoSizer: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView { TrackingView() }

    func updateNSView(_ nsView: NSView, context: Context) {
        (nsView as? TrackingView)?.scheduleResize()
    }

    final class TrackingView: NSView {
        private var anchoredWindow: NSWindow?
        private var anchorTop: CGFloat?
        /// Tracks visibility TRANSITIONS: the anchor is recorded on the first
        /// observation after the window becomes visible — by whichever runs
        /// first, the occlusion notification or a scheduled resize — because
        /// that is when the system has just positioned the panel under the
        /// status item (a reopen may be on another display). Anchoring at
        /// view-attach time recorded a not-yet-positioned frame, and
        /// re-anchoring on every occlusion event could persist a frame this
        /// shim had itself already moved.
        private var wasVisible = false
        private var resizePending = false
        private var visibilityObserver: NSObjectProtocol?

        deinit {
            if let visibilityObserver {
                NotificationCenter.default.removeObserver(visibilityObserver)
            }
        }

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            observeWindowIfNeeded()
        }

        override func layout() {
            super.layout()
            scheduleResize()
        }

        private func observeWindowIfNeeded() {
            guard let window, window !== anchoredWindow else { return }
            anchoredWindow = window
            anchorTop = nil
            wasVisible = false
            if let visibilityObserver {
                NotificationCenter.default.removeObserver(visibilityObserver)
            }
            visibilityObserver = NotificationCenter.default.addObserver(
                forName: NSWindow.didChangeOcclusionStateNotification,
                object: window, queue: .main
            ) { [weak self, weak window] _ in
                guard let self, let window else { return }
                if window.isVisible {
                    self.reanchorIfShowTransition()
                    self.scheduleResize()
                } else {
                    self.wasVisible = false
                }
            }
        }

        private func reanchorIfShowTransition() {
            guard let window, window.isVisible, !wasVisible else { return }
            anchorTop = window.frame.maxY
            wasVisible = true
        }

        /// Coalesces to one setFrame per runloop turn: setFrame is re-entrant
        /// with layout(), and a profile switch produces several layout passes;
        /// applying once after SwiftUI has settled avoids the frame fights.
        func scheduleResize() {
            guard !resizePending else { return }
            resizePending = true
            DispatchQueue.main.async { [weak self] in
                self?.resizePending = false
                self?.resizeWindowToFit()
            }
        }

        private func resizeWindowToFit() {
            observeWindowIfNeeded()
            reanchorIfShowTransition()
            // Pre-show frames belong to the system's placement pass.
            guard let window, let anchorTop, window.isVisible else { return }
            // This view is the root VStack's background, so its own laid-out
            // height IS the content's ideal height — even while the window is
            // stuck at another size. (contentView.fittingSize just echoes the
            // current frame for hosting views, which is why it can't detect
            // slack.)
            let ideal = bounds.height
            guard ideal > 1 else { return }
            var frame = window.frame
            frame.origin.y = anchorTop - ideal
            frame.size.height = ideal
            guard abs(frame.maxY - window.frame.maxY) > 1
                || abs(frame.height - window.frame.height) > 1 else { return }
            window.setFrame(frame, display: true, animate: false)
        }
    }
}

/// Catalog §2.4: layout only — the row's facts arrive in a ConnectorRow and
/// its two actions go back through the closures.
struct MCPRow: View {
    let row: ConnectorRow
    var onToggle: (Bool) -> Void
    var onEdit: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Toggle("", isOn: Binding(get: { row.enabled }, set: onToggle))
                .toggleStyle(.switch)
                .controlSize(.small)
                .labelsHidden()
            Text(row.name).fontWeight(.medium)
                .lineLimit(1)
                .layoutPriority(1)
            if let warning = row.toolWarning {
                // Advisory only: the switch above stays live and the row height is
                // unchanged. The tooltip sends the user to the editor's full note.
                Image(systemName: PopoverModel.toolWarningGlyph)
                    .imageScale(.small)
                    .foregroundStyle(.orange)
                    .help(warning)
                    .accessibilityLabel(warning)
            }
            Spacer()
            Button {
                onEdit()
            } label: {
                Image(systemName: "pencil")
                    .imageScale(.medium)
                    .foregroundStyle(.secondary)
            }
            .buttonStyle(.accessoryBar)
            .help(row.editTooltip)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 7)
    }
}

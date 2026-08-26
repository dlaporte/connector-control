import SwiftUI
import ConnectorControlCore

struct PopoverView: View {
    @EnvironmentObject var state: AppState
    @Environment(\.openWindow) private var openWindow
    @Environment(\.openSettings) private var openSettings

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider()
            if let error = state.lastError { errorBanner(error) }
            mcpList
            if state.needsClaudeRestart || state.applyRetryNeeded {
                Divider()
                footer
            }
        }
        .frame(minWidth: 240, maxWidth: 380)
        .background(WindowAutoSizer())
        .onAppear { state.reload() }
    }

    private func openEditor(_ target: EditTarget) {
        openWindow(id: "editor", value: target)
        NSApp.activate(ignoringOtherApps: true)
    }

    private var header: some View {
        HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 1) {
                Text("Connector Control").font(.headline)
                Text(headerSubtitle).font(.caption2).foregroundStyle(.secondary)
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
                .help("Add Connector")
                Button {
                    NSApp.activate(ignoringOtherApps: true)
                    openSettings()
                } label: {
                    headerIcon("gearshape")
                }
                .buttonStyle(.accessoryBar)
                .help("Settings")
                Button {
                    state.quitApp()
                } label: {
                    headerIcon("power")
                }
                .buttonStyle(.accessoryBar)
                .help("Quit Connector Control")
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.quinary)
    }

    private var profileChip: some View {
        Menu {
            ForEach(state.profileNames, id: \.self) { name in
                Button {
                    state.switchProfile(to: name)
                } label: {
                    if name == state.activeProfile {
                        Label(name, systemImage: "checkmark")
                    } else {
                        Text(name)
                    }
                }
            }
            Divider()
            Button("New Profile\u{2026}") { state.newProfile() }
            Button("Rename \u{201C}\(state.activeProfile)\u{201D}\u{2026}") { state.renameProfile() }
            Button("Delete \u{201C}\(state.activeProfile)\u{201D}\u{2026}") { state.deleteProfile() }
                .disabled(state.profileNames.count < 2)
        } label: {
            Text("\(state.activeProfile) \u{25BE}")
                .font(.caption2.weight(.semibold))
                .foregroundStyle(.secondary)
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .padding(.top, 1)
    }

    private var headerSubtitle: String {
        let total = state.store.mcps.count
        let enabled = state.store.mcps.values.filter(\.enabled).count
        return total == 0 ? "No connectors configured" : "\(enabled) of \(total) enabled"
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
                ForEach(state.sortedNames, id: \.self) { name in
                    MCPRow(name: name) {
                        if let entry = state.store.mcps[name] {
                            openEditor(.existing(name: name, entry: entry))
                        }
                    }
                    Divider()
                }
                if state.store.mcps.isEmpty {
                    Text("No connectors configured yet — add one below.")
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
            if state.applyRetryNeeded {
                Button {
                    state.apply()
                } label: {
                    Label("Apply Failed — Retry",
                          systemImage: "exclamationmark.arrow.circlepath")
                }
                .buttonStyle(.borderedProminent)
                .tint(.red)
                .controlSize(.small)
            } else if state.needsClaudeRestart {
                Button {
                    state.restartClaude()
                } label: {
                    Label("Restart Required", systemImage: "arrow.clockwise")
                }
                .buttonStyle(.borderedProminent)
                .tint(.orange)
                .controlSize(.small)
            }
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
        private var resizePending = false
        private var visibilityObserver: NSObjectProtocol?

        deinit {
            if let visibilityObserver {
                NotificationCenter.default.removeObserver(visibilityObserver)
            }
        }

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            reanchorIfNeeded()
        }

        override func layout() {
            super.layout()
            scheduleResize()
        }

        /// Records the top-edge anchor when the view lands in a window, and
        /// re-records it every time the window is shown — the system has just
        /// positioned the panel under the status item at those moments (a
        /// reopen may be on another display), so the frame is trustworthy in
        /// a way mid-content-change frames are not.
        private func reanchorIfNeeded() {
            guard let window, window !== anchoredWindow else { return }
            anchoredWindow = window
            anchorTop = window.frame.maxY
            if let visibilityObserver {
                NotificationCenter.default.removeObserver(visibilityObserver)
            }
            visibilityObserver = NotificationCenter.default.addObserver(
                forName: NSWindow.didChangeOcclusionStateNotification,
                object: window, queue: .main
            ) { [weak self, weak window] _ in
                guard let self, let window, window.isVisible else { return }
                self.anchorTop = window.frame.maxY
                self.scheduleResize()
            }
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
            reanchorIfNeeded()
            // Pre-show frames belong to the system's placement pass; the
            // show-time occlusion notification re-anchors and snaps.
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

struct MCPRow: View {
    @EnvironmentObject var state: AppState
    let name: String
    var onEdit: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Toggle("", isOn: Binding(
                get: { state.store.mcps[name]?.enabled ?? false },
                set: { state.setEnabled(name, $0) }))
                .toggleStyle(.switch)
                .controlSize(.small)
                .labelsHidden()
            Text(name).fontWeight(.medium)
                .lineLimit(1)
                .layoutPriority(1)
            Spacer()
            Button {
                onEdit()
            } label: {
                Image(systemName: "pencil")
                    .imageScale(.medium)
                    .foregroundStyle(.secondary)
            }
            .buttonStyle(.accessoryBar)
            .help("Edit “\(name)”")
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 7)
    }
}

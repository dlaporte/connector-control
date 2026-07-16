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
            if !state.missingEnabled.isEmpty { missingBanner }
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

    private var headerSubtitle: String {
        let total = state.store.mcps.count
        let enabled = state.store.mcps.values.filter(\.enabled).count
        return total == 0 ? "No connectors configured" : "\(enabled) of \(total) enabled"
    }

    private var missingBanner: some View {
        VStack(alignment: .leading, spacing: 6) {
            Label("Claude's config is missing \(state.missingEnabled.count) connector(s): "
                  + state.missingEnabled.joined(separator: ", "),
                  systemImage: "exclamationmark.triangle.fill")
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Button("Restore") { state.restoreMissing() }
                Button("Mark Disabled") { state.markMissingDisabled() }
            }
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.yellow.opacity(0.15))
    }

    private func errorBanner(_ message: String) -> some View {
        Label(message, systemImage: "xmark.octagon.fill")
            .font(.callout)
            .foregroundStyle(.red)
            .fixedSize(horizontal: false, vertical: true)
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var mcpList: some View {
        // A plain VStack, not a ScrollView: the MenuBarExtra window sizes to the
        // content's IDEAL height, and a ScrollView's ideal height is zero — the
        // list rendered fully collapsed. The popover now grows with the list.
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
/// shim snaps the window frame to the content's fitted height, anchored at the
/// top edge since the window hangs from the menu bar.
private struct WindowAutoSizer: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView { TrackingView() }

    func updateNSView(_ nsView: NSView, context: Context) {
        DispatchQueue.main.async {
            (nsView as? TrackingView)?.resizeWindowToFit()
        }
    }

    final class TrackingView: NSView {
        override func layout() {
            super.layout()
            resizeWindowToFit()
        }

        func resizeWindowToFit() {
            guard let window else { return }
            // This view is the root VStack's background, so its own laid-out
            // height IS the content's ideal height — even while the window is
            // stuck taller. (contentView.fittingSize just echoes the current
            // frame for hosting views, which is why it couldn't detect slack.)
            let ideal = bounds.height
            let current = window.contentView?.frame.height ?? window.frame.height
            guard ideal > 1, current - ideal > 1 else { return }
            var frame = window.frame
            let delta = current - ideal
            frame.origin.y += delta
            frame.size.height -= delta
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

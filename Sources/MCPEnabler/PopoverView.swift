import SwiftUI
import MCPEnablerCore

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
            if state.isDirty || state.needsClaudeRestart {
                Divider()
                footer
            }
        }
        .frame(minWidth: 240, maxWidth: 380)
        .onAppear { state.reload() }
        .confirmationDialog(
            "Apply these changes to Claude's config?\n"
            + (state.pendingApplyChanges ?? []).joined(separator: "\n"),
            isPresented: Binding(get: { state.pendingApplyChanges != nil },
                                 set: { if !$0 { state.cancelApply() } }),
            titleVisibility: .visible
        ) {
            Button("Apply") { state.confirmApply() }
            Button("Cancel", role: .cancel) { state.cancelApply() }
        }
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
                .help("Add MCP")
                Button {
                    NSApp.activate(ignoringOtherApps: true)
                    openSettings()
                } label: {
                    headerIcon("gearshape")
                }
                .buttonStyle(.accessoryBar)
                .help("Settings")
                Button {
                    NSApp.terminate(nil)
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
        return total == 0 ? "No MCPs configured" : "\(enabled) of \(total) enabled"
    }

    private var missingBanner: some View {
        VStack(alignment: .leading, spacing: 6) {
            Label("Claude's config is missing \(state.missingEnabled.count) MCP(s): "
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
                Text("No MCPs configured yet — add one below.")
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding()
            }
        }
    }

    private var footer: some View {
        HStack {
            Spacer()
            if state.isDirty {
                Button("Apply") { state.apply() }
                    .keyboardShortcut(.defaultAction)
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

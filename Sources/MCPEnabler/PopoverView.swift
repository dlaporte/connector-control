import SwiftUI
import MCPEnablerCore

struct PopoverView: View {
    @EnvironmentObject var state: AppState
    @Environment(\.openWindow) private var openWindow
    @Environment(\.openSettings) private var openSettings

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if !state.missingEnabled.isEmpty { missingBanner }
            if let error = state.lastError { errorBanner(error) }
            mcpList
            Divider()
            footer
        }
        .frame(width: 380)
        .onAppear { state.reload() }
    }

    private func openEditor(_ target: EditTarget) {
        openWindow(id: "editor", value: target)
        NSApp.activate(ignoringOtherApps: true)
    }

    private var missingBanner: some View {
        VStack(alignment: .leading, spacing: 6) {
            Label("Claude's config is missing \(state.missingEnabled.count) MCP(s): "
                  + state.missingEnabled.joined(separator: ", "),
                  systemImage: "exclamationmark.triangle.fill")
                .font(.callout)
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
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var mcpList: some View {
        // A plain VStack, not a ScrollView: the MenuBarExtra window sizes to the
        // content's IDEAL height, and a ScrollView's ideal height is zero — the
        // list rendered fully collapsed. The popover now grows with the list.
        VStack(spacing: 0) {
            ForEach(state.sortedNames, id: \.self) { name in
                MCPRow(name: name)
                Divider()
            }
            if state.store.mcps.isEmpty {
                Text("No MCPs configured yet — add one below.")
                    .foregroundStyle(.secondary)
                    .padding()
            }
        }
    }

    private var footer: some View {
        HStack {
            Menu("＋ Add") {
                Button("Remote Server…") {
                    openEditor(.newRemote())
                }
                Button("Local Server…") {
                    openEditor(.new(template: .object([
                        "command": .string("npx"),
                        "args": .array([.string("-y"), .string("")]),
                    ])))
                }
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            Menu("Edit") {
                ForEach(state.sortedNames, id: \.self) { name in
                    Button(name) {
                        if let entry = state.store.mcps[name] {
                            openEditor(.existing(name: name, entry: entry))
                        }
                    }
                }
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .disabled(state.store.mcps.isEmpty)
            Spacer()
            if state.showRestartPrompt {
                Button("Restart Claude") { state.restartClaude() }
                Button("Later") { state.showRestartPrompt = false }
            } else if state.isDirty {
                Button("Apply") { state.apply() }
                    .keyboardShortcut(.defaultAction)
            }
            Button {
                NSApp.activate(ignoringOtherApps: true)
                openSettings()
            } label: {
                Image(systemName: "gearshape")
            }
            .buttonStyle(.plain)
            .help("Settings")
            Button("Quit") { NSApp.terminate(nil) }
        }
        .padding(10)
    }
}

struct MCPRow: View {
    @EnvironmentObject var state: AppState
    let name: String

    var body: some View {
        HStack(spacing: 10) {
            Toggle("", isOn: Binding(
                get: { state.store.mcps[name]?.enabled ?? false },
                set: { state.setEnabled(name, $0) }))
                .toggleStyle(.switch)
                .controlSize(.small)
                .labelsHidden()
            Text(name).fontWeight(.medium)
            Spacer()
            if let config = state.store.mcps[name]?.config,
               RemotePattern.detect(config) != nil {
                Text("REMOTE").font(.caption2).foregroundStyle(.secondary)
                    .padding(.horizontal, 5).padding(.vertical, 1)
                    .overlay(RoundedRectangle(cornerRadius: 4)
                        .stroke(.secondary.opacity(0.4)))
            } else {
                Text("LOCAL").font(.caption2).foregroundStyle(.secondary)
                    .padding(.horizontal, 5).padding(.vertical, 1)
                    .overlay(RoundedRectangle(cornerRadius: 4)
                        .stroke(.secondary.opacity(0.4)))
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 7)
    }
}

import SwiftUI
import MCPEnablerCore

struct PopoverView: View {
    @EnvironmentObject var state: AppState
    @State private var editTarget: EditTarget?
    @State private var showRestore = false

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
        .sheet(item: $editTarget) { target in
            EditSheetView(target: target).environmentObject(state)
        }
        .sheet(isPresented: $showRestore) {
            RestoreSheetView().environmentObject(state)
        }
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
        ScrollView {
            VStack(spacing: 0) {
                ForEach(state.sortedNames, id: \.self) { name in
                    MCPRow(name: name) {
                        if let entry = state.store.mcps[name] {
                            editTarget = .existing(name: name, entry: entry)
                        }
                    }
                    Divider()
                }
                if state.store.mcps.isEmpty {
                    Text("No MCPs configured yet — add one below.")
                        .foregroundStyle(.secondary)
                        .padding()
                }
            }
        }
        .frame(maxHeight: 320)
    }

    private var footer: some View {
        HStack {
            Menu("＋ Add") {
                Button("Remote Server…") {
                    editTarget = .new(template: RemotePattern.make(url: ""))
                }
                Button("Local Server…") {
                    editTarget = .new(template: .object([
                        "command": .string("npx"),
                        "args": .array([.string("-y"), .string("")]),
                    ]))
                }
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            Menu("Backups") {
                Button("Reveal in Finder") {
                    NSWorkspace.shared.activateFileViewerSelecting(
                        [state.service.backups.backupsDir])
                }
                Button("Restore…") { showRestore = true }
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            Spacer()
            if state.showRestartPrompt {
                Button("Restart Claude") { /* wired in Task 13 */ }
                Button("Later") { state.showRestartPrompt = false }
            } else if state.isDirty {
                Button("Apply") { state.apply() }
                    .keyboardShortcut(.defaultAction)
            }
            Button("Quit") { NSApp.terminate(nil) }
        }
        .padding(10)
    }
}

struct MCPRow: View {
    @EnvironmentObject var state: AppState
    let name: String
    let onEdit: () -> Void

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
            Button(action: onEdit) {
                Image(systemName: "chevron.right").foregroundStyle(.secondary)
            }.buttonStyle(.plain)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 7)
    }
}

import SwiftUI
import MCPEnablerCore

struct RestoreSheetView: View {
    @EnvironmentObject var state: AppState
    @Environment(\.dismiss) private var dismiss
    @State private var backups: [URL] = []
    @State private var selection: URL?
    @State private var confirming = false
    @State private var restoreError: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Restore Claude config from a backup").font(.headline)
            Text("The current file is backed up first, then replaced by the "
                 + "selected backup.")
                .font(.caption).foregroundStyle(.secondary)
            List(backups, id: \.self, selection: $selection) { url in
                Text(url.lastPathComponent).font(.system(.callout, design: .monospaced))
            }
            .frame(height: 180)
            if let restoreError {
                Text(restoreError).font(.callout).foregroundStyle(.red)
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Restore…") { confirming = true }
                    .disabled(selection == nil)
            }
        }
        .padding(16)
        .frame(width: 460)
        .onAppear {
            var found = (try? state.service.backups.backups(
                series: "claude_desktop_config")) ?? []
            let original = state.service.backups.backupsDir
                .appendingPathComponent("claude_desktop_config.original.json")
            if FileManager.default.fileExists(atPath: original.path) {
                found.append(original)
            }
            backups = found
        }
        .confirmationDialog(
            "Replace Claude's config with \(selection?.lastPathComponent ?? "")?",
            isPresented: $confirming, titleVisibility: .visible
        ) {
            Button("Restore", role: .destructive) {
                guard let backup = selection else { return }
                do {
                    try state.service.restoreClaudeConfig(
                        from: backup, mergedWith: state.store)
                    UserDefaults.standard.set(Date(), forKey: "lastApplyDate")
                    state.reload()
                    dismiss()
                } catch {
                    restoreError = error.localizedDescription
                    state.lastError = error.localizedDescription
                }
            }
        }
    }
}

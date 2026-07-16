import SwiftUI
import AppKit
import UniformTypeIdentifiers
import ServiceManagement
import MCPEnablerCore

struct SettingsView: View {
    @EnvironmentObject var state: AppState
    @State private var showRestore = false
    @AppStorage("masterStoreDir") private var masterStoreDirSetting: String = ""
    @AppStorage("restartBehavior") private var restartBehavior: String = "ask"
    @AppStorage("claudeAppPath") private var claudeAppPath: String = "/Applications/Claude.app"
    @AppStorage("backupKeepCount") private var backupKeepCount: Int = 20
    @AppStorage("notifyExternalChanges") private var notifyExternalChanges: Bool = true

    var body: some View {
        TabView {
            generalTab
                .tabItem { Label("General", systemImage: "gearshape") }
            storageTab
                .tabItem { Label("Storage", systemImage: "externaldrive") }
            claudeTab
                .tabItem { Label("Claude", systemImage: "bubble.left.and.bubble.right") }
        }
        .padding(20)
        .frame(width: 430)
        .sheet(isPresented: $showRestore) {
            RestoreSheetView().environmentObject(state)
        }
    }

    private var generalTab: some View {
        Form {
            Toggle("Launch at login", isOn: Binding(
                get: { SMAppService.mainApp.status == .enabled },
                set: { on in
                    do {
                        if on { try SMAppService.mainApp.register() }
                        else { try SMAppService.mainApp.unregister() }
                    } catch {
                        state.lastError = "Launch at login needs the built app "
                            + "bundle (run scripts/build-app.sh): \(error.localizedDescription)"
                    }
                }))

            Picker("After Apply:", selection: $restartBehavior) {
                Text("Ask to restart Claude").tag("ask")
                Text("Restart Claude automatically").tag("auto")
                Text("Do nothing").tag("never")
            }

            Toggle("Notify when Claude's config changes externally",
                   isOn: $notifyExternalChanges)
        }
        .padding(.top, 8)
    }

    private var storageTab: some View {
        Form {
            Section("Master List Location") {
                Text("MCP list location: \(state.service.paths.storeDirURL.path)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                    .truncationMode(.head)
                HStack {
                    Button("Choose…") { chooseStoreDir() }
                    Button("Use Default") { state.repointStore(to: nil) }
                        .disabled(masterStoreDirSetting.isEmpty)
                }
                Stepper(
                    "Keep last \(backupKeepCount) backups of each file",
                    value: $backupKeepCount, in: 5...100)
                .onChange(of: backupKeepCount) { _, _ in state.refreshServiceSettings() }
            }

            Section("Backups") {
                Text("Both config files are backed up automatically before every change.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                HStack {
                    Button("Reveal in Finder") {
                        NSWorkspace.shared.activateFileViewerSelecting(
                            [state.service.backups.backupsDir])
                    }
                    Button("Restore…") { showRestore = true }
                }
            }
        }
        .padding(.top, 8)
    }

    private var claudeTab: some View {
        Form {
            Section("Claude App") {
                Text("App location: \(claudeAppPath)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                    .truncationMode(.head)
                HStack {
                    Button("Choose…") { chooseClaudeApp() }
                    Button("Use Default") { claudeAppPath = "/Applications/Claude.app" }
                        .disabled(claudeAppPath == "/Applications/Claude.app")
                }
            }
        }
        .padding(.top, 8)
    }

    private func chooseStoreDir() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = "Choose"
        if panel.runModal() == .OK, let url = panel.url {
            state.repointStore(to: url)
        }
    }

    private func chooseClaudeApp() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        panel.allowsMultipleSelection = false
        panel.allowedContentTypes = [.applicationBundle]
        panel.directoryURL = URL(fileURLWithPath: "/Applications")
        panel.prompt = "Choose"
        if panel.runModal() == .OK, let url = panel.url {
            claudeAppPath = url.path
        }
    }
}

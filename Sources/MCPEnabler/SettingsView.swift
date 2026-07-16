import SwiftUI
import AppKit
import UniformTypeIdentifiers
import ServiceManagement
import MCPEnablerCore

struct SettingsView: View {
    @EnvironmentObject var state: AppState
    @State private var showRestore = false
    @State private var launchAtLogin = SMAppService.mainApp.status == .enabled
    @State private var loginItemNote: String?
    @AppStorage("masterStoreDir") private var masterStoreDirSetting: String = ""
    @AppStorage("restartBehavior") private var restartBehavior: String = "ask"
    @AppStorage("claudeAppPath") private var claudeAppPath: String = "/Applications/Claude.app"
    @AppStorage("backupKeepCount") private var backupKeepCount: Int = 20
    @AppStorage("notifyExternalChanges") private var notifyExternalChanges: Bool = true
    @AppStorage("confirmBeforeApply") private var confirmBeforeApply: Bool = false

    var body: some View {
        TabView {
            generalTab
                .tabItem { Label("General", systemImage: "gearshape") }
            storageTab
                .tabItem { Label("Storage", systemImage: "externaldrive") }
            claudeTab
                .tabItem { Label("Claude", systemImage: "bubble.left.and.bubble.right") }
        }
        .frame(width: 480)
        .sheet(isPresented: $showRestore) {
            RestoreSheetView().environmentObject(state)
        }
    }

    private var generalTab: some View {
        Form {
            Section {
                Toggle("Launch at login", isOn: $launchAtLogin)
                    .onChange(of: launchAtLogin) { _, wantOn in
                        let isOn = SMAppService.mainApp.status == .enabled
                        guard wantOn != isOn else { return }
                        do {
                            if wantOn { try SMAppService.mainApp.register() }
                            else { try SMAppService.mainApp.unregister() }
                            loginItemNote = nil
                        } catch {
                            launchAtLogin = isOn   // revert the checkbox
                            loginItemNote = "Couldn't update login item: \(error.localizedDescription)"
                        }
                        if wantOn, SMAppService.mainApp.status == .requiresApproval {
                            loginItemNote = "Approve MCP Enabler under System Settings → General → Login Items."
                        }
                    }
                if let loginItemNote {
                    Text(loginItemNote).font(.caption).foregroundStyle(.secondary)
                }
            }

            Section {
                Picker("After Apply:", selection: $restartBehavior) {
                    Text("Ask to restart Claude").tag("ask")
                    Text("Restart Claude automatically").tag("auto")
                    Text("Do nothing").tag("never")
                }

                Toggle("Confirm before Apply", isOn: $confirmBeforeApply)
            }

            Section {
                Toggle("Notify when Claude's config changes externally",
                       isOn: $notifyExternalChanges)
            }
        }
        .formStyle(.grouped)
        .onAppear { launchAtLogin = SMAppService.mainApp.status == .enabled }
    }

    private var storageTab: some View {
        Form {
            Section("Master List Location") {
                Text(state.service.paths.storeDirURL.path)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                HStack {
                    Button("Choose…") { chooseStoreDir() }
                    Button("Use Default") { state.repointStore(to: nil) }
                        .disabled(masterStoreDirSetting.isEmpty)
                }
                Stepper(value: $backupKeepCount, in: 5...100) {
                    Text("Keep \(backupKeepCount) backups of each file")
                }
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
        .formStyle(.grouped)
    }

    private var claudeTab: some View {
        Form {
            Section("Claude App") {
                Text(claudeAppPath)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                HStack {
                    Button("Choose…") { chooseClaudeApp() }
                    Button("Use Default") { claudeAppPath = "/Applications/Claude.app" }
                        .disabled(claudeAppPath == "/Applications/Claude.app")
                }
            }
        }
        .formStyle(.grouped)
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

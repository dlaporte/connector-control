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
    
    @AppStorage("claudeAppPath") private var claudeAppPath: String = "/Applications/Claude.app"
    @AppStorage("backupKeepCount") private var backupKeepCount: Int = 20
    @AppStorage("notifyExternalChanges") private var notifyExternalChanges: Bool = true
    @AppStorage("confirmBeforeRestart") private var confirmBeforeRestart: Bool = true
    @AppStorage("confirmBeforeQuit") private var confirmBeforeQuit: Bool = true

    var body: some View {
        TabView {
            generalTab
                .tabItem { Label("General", systemImage: "gearshape") }
            storageTab
                .tabItem { Label("Storage", systemImage: "externaldrive") }
            claudeTab
                .tabItem {
                    Label {
                        Text("Claude")
                    } icon: {
                        Image(nsImage: claudeTabIcon)
                    }
                }
            aboutTab
                .tabItem { Label("About", systemImage: "info.circle") }
        }
        // Tall enough that the largest tab (Storage) fits without scrolling.
        .frame(width: 480, height: 340)
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
                            loginItemNote = "Approve Connector Control under System Settings → General → Login Items."
                        }
                    }
                if let loginItemNote {
                    Text(loginItemNote).font(.caption).foregroundStyle(.secondary)
                }
            }

            Section {
                Toggle("Confirm before restarting Claude", isOn: $confirmBeforeRestart)
                Toggle("Confirm before quitting", isOn: $confirmBeforeQuit)
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
            }

            Section("Backups") {
                Text("Both config files are backed up automatically before every change.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Stepper(value: $backupKeepCount, in: 5...100) {
                    Text("Keep \(backupKeepCount) backups of each file")
                }
                .onChange(of: backupKeepCount) { _, _ in state.refreshServiceSettings() }
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

    private var aboutTab: some View {
        VStack(spacing: 8) {
            Image(nsImage: NSApp.applicationIconImage)
                .resizable()
                .scaledToFit()
                .frame(width: 72, height: 72)
            Text("Connector Control")
                .font(.title2.bold())
            Text("Version \(appVersion)")
                .font(.callout)
                .foregroundStyle(.secondary)
            Text("Manages the custom MCP connectors in Claude Desktop's "
                 + "configuration, with automatic backups of every change.")
                .font(.callout)
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
                // Wrap to the window width instead of laying out at ideal
                // (single-line) width.
                .fixedSize(horizontal: false, vertical: true)
                .padding(.top, 4)
            Divider()
                .padding(.vertical, 6)
            Text("David LaPorte")
                .font(.caption)
        }
        .padding(28)
        .frame(maxWidth: .infinity)
    }

    private var appVersion: String {
        let info = Bundle.main.infoDictionary
        guard let short = info?["CFBundleShortVersionString"] as? String else {
            return "development build"
        }
        if let build = info?["CFBundleVersion"] as? String, build != short {
            return "\(short) (\(build))"
        }
        return short
    }

    /// The installed Claude app's own icon (falls back to a generic app icon
    /// when Claude isn't at the configured path).
    private var claudeTabIcon: NSImage {
        let icon = NSWorkspace.shared.icon(forFile: claudeAppPath)
        icon.size = NSSize(width: 22, height: 22)
        return icon
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

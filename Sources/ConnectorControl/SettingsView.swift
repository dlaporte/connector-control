import SwiftUI
import AppKit
import CoreImage
import UniformTypeIdentifiers
import ConnectorControlCore
import ConnectorControlState

/// Catalog §4: three tabs of bindings; every rule and string is SettingsModel's.
struct SettingsView: View {
    @StateObject private var model: SettingsModel
    private let state: AppState
    @State private var showRestore = false
    /// Decoded once here and again only when the app path changes: the tab
    /// bar re-renders on every model change, and the icon comes from disk
    /// (or a Core Image pass over the app icon).
    @State private var claudeTabIcon: NSImage

    init(state: AppState, settings: AppSettings, autostart: Autostart, updater: Updater) {
        self.state = state
        _model = StateObject(wrappedValue: SettingsModel(state: state, settings: settings, autostart: autostart, updater: updater))
        _claudeTabIcon = State(initialValue: SettingsView.makeClaudeTabIcon(
            appPath: settings.claudeAppPath ?? AppState.defaultClaudeAppPath))
    }

    var body: some View {
        TabView {
            generalTab
                .tabItem { Label(SettingsModel.generalTab, systemImage: "gearshape") }
            storageTab
                .tabItem { Label(SettingsModel.storageTab, systemImage: "externaldrive") }
            claudeTab
                .tabItem {
                    Label {
                        Text(SettingsModel.claudeTab)
                    } icon: {
                        Image(nsImage: claudeTabIcon)
                    }
                }
        }
        // Tall enough that the largest tab (Claude, with the Tools section
        // in its worst case: every tool found only in the shell) fits
        // without scrolling; the Windows Settings window uses the same size.
        .frame(width: 480, height: 560)
        .sheet(isPresented: $showRestore) {
            RestoreSheetView(state: state)
        }
        .onChange(of: model.claudeAppPath) {
            claudeTabIcon = SettingsView.makeClaudeTabIcon(appPath: model.claudeAppPath)
        }
    }

    private var generalTab: some View {
        Form {
            Section {
                Toggle(SettingsModel.launchAtLoginTitle, isOn: $model.launchAtLogin)
                if let note = model.loginItemNote {
                    Text(note).font(.caption).foregroundStyle(.secondary)
                }
            }

            Section {
                Toggle(SettingsModel.confirmRestartTitle, isOn: $model.confirmBeforeRestart)
                Toggle(SettingsModel.confirmQuitTitle, isOn: $model.confirmBeforeQuit)
            }

            Section {
                Toggle(SettingsModel.notifyTitle, isOn: $model.notifyExternalChanges)
                Text(SettingsModel.notifyCaption)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section(SettingsModel.updatesHeader) {
                Toggle(SettingsModel.autoUpdateTitle, isOn: $model.autoUpdate)
                    .disabled(!model.updatesEnabled)
                HStack {
                    Text(model.versionText)
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button(SettingsModel.checkForUpdatesTitle) { model.checkForUpdates() }
                        .disabled(!model.updatesEnabled)
                }
            }
        }
        .formStyle(.grouped)
        .onAppear { model.refresh() }
    }

    private var storageTab: some View {
        Form {
            Section(SettingsModel.masterListHeader) {
                Text(model.storeDirPath)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                HStack {
                    Button(SettingsModel.chooseTitle) { chooseStoreDir() }
                    Button(SettingsModel.useDefaultTitle) { model.useDefaultStoreDir() }
                        .disabled(!model.canUseDefaultStore)
                }
            }

            Section(SettingsModel.backupsHeader) {
                Text(SettingsModel.backupsCaption)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Stepper(value: $model.backupKeepCount,
                        in: SettingsModel.minKeepCount...SettingsModel.maxKeepCount) {
                    Text(model.keepCountLabel)
                }
                HStack {
                    Button(SettingsModel.revealInFinderTitle) {
                        NSWorkspace.shared.activateFileViewerSelecting([model.backupsDir])
                    }
                    Button(SettingsModel.restoreTitle) { showRestore = true }
                }
            }
        }
        .formStyle(.grouped)
    }

    private var claudeTab: some View {
        Form {
            Section(SettingsModel.claudeAppHeader) {
                Text(model.claudeAppPath)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                HStack {
                    Button(SettingsModel.chooseTitle) { chooseClaudeApp() }
                    Button(SettingsModel.useDefaultTitle) { model.useDefaultClaudeApp() }
                        .disabled(!model.canUseDefaultClaudeApp)
                }
            }

            Section(SettingsModel.toolsHeader) {
                Text(SettingsModel.toolsCaption)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                ForEach(model.toolRows, id: \.name) { row in
                    ToolRowView(row: row)
                }
            }
        }
        .formStyle(.grouped)
        // Spec §6 D4: the Mac refreshes when this tab appears.
        .onAppear { model.refreshTools() }
    }

    /// Claude's bare "splat" glyph, taken from the tray template image the
    /// Claude app ships. As a template it renders exactly like the SF-symbol
    /// tab icons. Falls back to a desaturated copy of the app icon if a future
    /// Claude version moves the asset.
    private static func makeClaudeTabIcon(appPath: String) -> NSImage {
        let resources = URL(fileURLWithPath: appPath)
            .appendingPathComponent("Contents/Resources")
        for name in ["TrayIconTemplate@2x.png", "TrayIconTemplate.png"] {
            let url = resources.appendingPathComponent(name)
            if let splat = NSImage(contentsOf: url) {
                splat.isTemplate = true
                splat.size = NSSize(width: 18, height: 18)
                return splat
            }
        }
        let icon = NSWorkspace.shared.icon(forFile: appPath)
        let size = NSSize(width: 22, height: 22)
        guard let tiff = icon.tiffRepresentation,
              let ciImage = CIImage(data: tiff),
              let filter = CIFilter(name: "CIColorControls",
                                    parameters: [kCIInputImageKey: ciImage,
                                                 kCIInputSaturationKey: 0])
        else {
            icon.size = size
            return icon
        }
        guard let output = filter.outputImage else {
            icon.size = size
            return icon
        }
        let rep = NSCIImageRep(ciImage: output)
        let gray = NSImage(size: rep.size)
        gray.addRepresentation(rep)
        gray.size = size
        return gray
    }

    private func chooseStoreDir() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = "Choose"
        if panel.runModal() == .OK, let url = panel.url {
            model.chooseStoreDir(url)
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
            model.chooseClaudeApp(url)
        }
    }
}

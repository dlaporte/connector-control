import SwiftUI
import AppKit
import CoreImage
import ConnectorControlState

/// Which tab Settings shows.
enum SettingsTab: Hashable {
    case general, storage, claude
}

/// Three tabs of bindings; every rule and string is SettingsModel's. Every row has one shape: what it is on the
/// left with an optional note under it, the control on the right; a note about a whole group is its footer.
struct SettingsView: View {
    @StateObject private var model: SettingsModel
    private let state: AppState
    @State private var showRestore = false
    @State private var tab: SettingsTab
    /// Decoded once here and again only when the app path changes: the tab
    /// bar re-renders on every model change, and the icon comes from disk
    /// (or a Core Image pass over the app icon).
    @State private var claudeTabIcon: NSImage

    /// `tab` is the one shown first: General, as the app opens it; a test can open another.
    init(state: AppState, settings: AppSettings, autostart: Autostart, updater: Updater, commandLineTool: CommandLineTool?,
         tab: SettingsTab = .general) {
        self.state = state
        _model = StateObject(wrappedValue: SettingsModel(state: state, settings: settings, autostart: autostart, updater: updater,
                                                         commandLineTool: commandLineTool))
        _tab = State(initialValue: tab)
        _claudeTabIcon = State(initialValue: SettingsView.makeClaudeTabIcon(
            appPath: settings.claudeAppPath ?? AppState.defaultClaudeAppPath))
    }

    var body: some View {
        TabView(selection: $tab) {
            generalTab
                .tabItem { Label(SettingsModel.generalTab, systemImage: "gearshape") }
                .tag(SettingsTab.general)
            storageTab
                .tabItem { Label(SettingsModel.storageTab, systemImage: "externaldrive") }
                .tag(SettingsTab.storage)
            claudeTab
                .tabItem {
                    Label {
                        Text(SettingsModel.claudeTab)
                    } icon: {
                        Image(nsImage: claudeTabIcon)
                    }
                }
                .tag(SettingsTab.claude)
        }
        // Tall enough that no tab scrolls: the Claude tab, the tallest, measures 637 with a missing tool
        // family in the Tools footer and a two-line amber note under the integration switch, and the rest
        // leaves room for one more wrapped note.
        // Platform idiom: the Windows window is the same width but taller (840): its Fluent rows, buttons and
        // tab strip are taller, its Claude App group has three rows, and its height includes the title bar.
        .frame(width: 480, height: 660)
        .sheet(isPresented: $showRestore) {
            RestoreSheetView(state: state)
        }
        .onChange(of: model.claudeApp) {
            claudeTabIcon = SettingsView.makeClaudeTabIcon(appPath: model.claudeApp.path)
        }
    }

    private var generalTab: some View {
        Form {
            Section(SettingsModel.startupHeader) {
                switchRow(SettingsModel.launchAtLoginTitle, isOn: $model.launchAtLogin,
                          note: model.loginItemNote, noteIsWarning: model.loginItemNoteIsWarning)
            }

            Section(SettingsModel.confirmationsHeader) {
                switchRow(SettingsModel.confirmRestartTitle, isOn: $model.confirmBeforeRestart)
                switchRow(SettingsModel.confirmQuitTitle, isOn: $model.confirmBeforeQuit)
            }

            Section(SettingsModel.notificationsHeader) {
                switchRow(SettingsModel.notifyTitle, isOn: $model.notifyExternalChanges, note: SettingsModel.notifyCaption)
            }

            Section(SettingsModel.updatesHeader) {
                switchRow(SettingsModel.autoUpdateTitle, isOn: $model.autoUpdate, enabled: model.updatesEnabled)
                SettingsRow {
                    Text(model.versionText)
                } note: {
                    if let caption = model.updatesCaption {
                        SettingsNote(caption)
                    }
                } control: {
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
            Section {
                SettingsRow {
                    PathLabel(path: model.storeDir.path, name: model.storeDirName)
                } note: {
                    SettingsNote(model.storeDirParent)
                } control: {
                    Button(SettingsModel.useDefaultTitle) { model.useDefaultStoreDir() }
                        .disabled(!model.canUseDefaultStore)
                    Button(SettingsModel.chooseButtonTitle) { chooseStoreDir() }
                }
            } header: {
                Text(SettingsModel.masterListHeader)
            } footer: {
                SettingsFooter(SettingsModel.masterListFooter)
            }

            Section {
                SettingsRow {
                    Text(SettingsModel.backupsTitle)
                } note: {
                    SettingsNote(SettingsModel.backupsCaption)
                } control: {
                    Text(model.backupKeepCount, format: .number)
                        .monospacedDigit()
                        .accessibilityHidden(true)
                    Stepper(SettingsModel.backupsTitle, value: $model.backupKeepCount,
                            in: SettingsModel.minKeepCount...SettingsModel.maxKeepCount)
                        .labelsHidden()
                        .accessibilityValue(Text(model.backupKeepCount, format: .number))
                        .accessibilityHint(SettingsModel.backupsCaption)
                }
                SettingsRow {
                    PathLabel(path: model.backupsDir.path, name: model.backupsDirName)
                } note: {
                    SettingsNote(model.backupsDirParent)
                } control: {
                    Button(SettingsModel.revealInFinderTitle) {
                        NSWorkspace.shared.activateFileViewerSelecting([model.backupsDir])
                    }
                    Button(SettingsModel.restoreButtonTitle) { showRestore = true }
                }
            } header: {
                Text(SettingsModel.backupsHeader)
            } footer: {
                SettingsFooter(SettingsModel.backupsFooter)
            }
        }
        .formStyle(.grouped)
    }

    private var claudeTab: some View {
        Form {
            Section(SettingsModel.claudeAppHeader) {
                SettingsRow {
                    PathLabel(path: model.claudeApp.path, name: model.claudeAppName)
                } note: {
                    SettingsNote(model.claudeAppNote)
                } control: {
                    Button(SettingsModel.useDefaultTitle) { model.useDefaultClaudeApp() }
                        .disabled(!model.canUseDefaultClaudeApp)
                    Button(SettingsModel.chooseButtonTitle) { chooseClaudeApp() }
                        .disabled(!model.canChooseClaudeApp)
                }
            }

            Section(SettingsModel.claudeIntegrationHeader) {
                // A dimmed switch dims its title and never the note under it, which says why.
                switchRow(SettingsModel.claudeIntegrationTitle, isOn: $model.claudeIntegrationEnabled,
                          note: model.claudeIntegrationCaptionText, noteIsWarning: model.claudeIntegrationCaptionIsWarning,
                          titleOpacity: model.claudeIntegrationTitleOpacity, enabled: model.canChangeClaudeIntegration)
                // The second switch keeps its value while the first is off, dimmed, and its note hides.
                switchRow(SettingsModel.confirmClaudeChangesTitle, isOn: $model.confirmClaudeChanges,
                          note: model.confirmClaudeChangesCaption, noteIsWarning: model.confirmClaudeChangesCaptionIsWarning,
                          titleOpacity: model.confirmClaudeChangesTitleOpacity, enabled: model.canChangeConfirmClaudeChanges)
                if let problem = model.controlEndpointProblem {
                    // Plain amber text with no glyph, unlike a tool note's caution mark, and like every note in
                    // these two groups not selectable: the Windows window draws them the same way, so both read alike.
                    SettingsNote(problem, isWarning: true)
                }
            }

            if model.hasCommandLineTool {
                Section(SettingsModel.commandLineToolHeader) {
                    SettingsRow {
                        Text(runs: model.commandLineToolStatus)
                    } note: {
                        if model.hasCommandLineToolCaption {
                            Text(runs: model.commandLineToolCaption)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        if let message = model.commandLineToolMessage {
                            Text(message)
                                .font(.caption)
                                .foregroundStyle(.red)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    } control: {
                        Button(model.commandLineToolButton) { model.commandLineToolAction() }
                            .disabled(!model.canUseCommandLineToolButton)
                    }
                }
            }

            Section {
                ForEach(model.toolRows, id: \.name) { row in
                    ToolRowView(row: row)
                }
            } header: {
                Text(SettingsModel.toolsHeader)
            } footer: {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(model.toolFooterLines, id: \.text) { line in
                        ToolFooterLineView(line: line)
                    }
                    Text(SettingsModel.toolsCaption)
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .formStyle(.grouped)
        // The Mac refreshes when this tab appears.
        .onAppear { model.refreshTools() }
    }

    /// An on/off row: the title, dimmed with the switch where it says so, the note under it, and the switch, which
    /// reads the note as its hint, so the reason it is dimmed or what it does is read with it.
    private func switchRow(_ title: String, isOn: Binding<Bool>, note: String? = nil, noteIsWarning: Bool = false,
                           titleOpacity: Double = 1, enabled: Bool = true) -> some View {
        SettingsRow {
            Text(title)
                .opacity(titleOpacity)
        } note: {
            if let note {
                SettingsNote(note, isWarning: noteIsWarning)
            }
        } control: {
            Toggle(title, isOn: isOn)
                .labelsHidden()
                .toggleStyle(.switch)
                .controlSize(.small)
                .disabled(!enabled)
                .accessibilityHint(note ?? "")
        }
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
                                    parameters: [kCIInputImageKey: ciImage, kCIInputSaturationKey: 0]),
              let output = filter.outputImage
        else {
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
        if let folder = FilePanels.chooseFolder() {
            model.chooseStoreDir(URL(fileURLWithPath: folder, isDirectory: true))
        }
    }

    private func chooseClaudeApp() {
        if let path = FilePanels.chooseApplication(), model.beginCheckingClaudeApp() {
            // The path is trusted at every Restart Claude from now on, so a
            // bundle that is not Claude signed by Anthropic is refused here,
            // with the reason, rather than at the next restart click. The
            // check reads the whole bundle, so the row says "Checking…" and
            // Choose… waits; the model is updated when it is done.
            let url = URL(fileURLWithPath: path)
            let model = self.model
            let dialogs = state.dialogs
            DispatchQueue.global().async {
                let problem = ClaudeRestarter.verifyIsClaude(at: url)
                DispatchQueue.main.async {
                    model.endCheckingClaudeApp()
                    if let problem {
                        // Not inside this main-queue block: a modal entered here would hold back every
                        // other one, the control socket's lines among them, until it is answered.
                        DialogConfirmer.nextRunLoopTurn {
                            dialogs.inform(message: SettingsModel.claudeAppRejectedTitle, informative: problem)
                        }
                    } else {
                        model.chooseClaudeApp(url)
                    }
                }
            }
        }
    }
}

/// One Settings row: what it is on the left, with an optional note under it, and the control on the right.
private struct SettingsRow<Title: View, Note: View, Control: View>: View {
    @ViewBuilder let title: Title
    @ViewBuilder let note: Note
    @ViewBuilder let control: Control

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            // The left side takes the room the control leaves, so a note wraps only when it must.
            VStack(alignment: .leading, spacing: 2) {
                title
                note
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            HStack(spacing: 6) {
                control
            }
            .fixedSize()
        }
    }
}

/// A row's note: secondary, amber when it is a warning.
private struct SettingsNote: View {
    let text: String
    let isWarning: Bool

    init(_ text: String, isWarning: Bool = false) {
        self.text = text
        self.isWarning = isWarning
    }

    var body: some View {
        Text(text)
            .font(.caption)
            .foregroundStyle(isWarning ? AnyShapeStyle(Color.orange) : AnyShapeStyle(.secondary))
            .fixedSize(horizontal: false, vertical: true)
    }
}

/// A note about a whole group, under its box.
private struct SettingsFooter: View {
    let text: String

    init(_ text: String) {
        self.text = text
    }

    var body: some View {
        Text(text)
            .font(.caption)
            .foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// A path row's first line: the item's own icon, as Finder shows it, and its name.
private struct PathLabel: View {
    let path: String
    let name: String

    var body: some View {
        HStack(spacing: 6) {
            Image(nsImage: NSWorkspace.shared.icon(forFile: path))
                .resizable()
                .frame(width: 16, height: 16)
                .accessibilityHidden(true)
            Text(name)
                .lineLimit(1)
                .truncationMode(.middle)
        }
    }
}

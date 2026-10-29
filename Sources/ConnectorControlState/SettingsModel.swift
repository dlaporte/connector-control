import Foundation
import Combine
import ConnectorControlCore

/// SettingsView state: three tabs, every toggle, path, and button.
/// Pass-through settings are computed properties that write the seam and send
/// objectWillChange (the C# Raise()).
@MainActor
public final class SettingsModel: ObservableObject {
    public static let generalTab = "General"
    public static let storageTab = "Storage"
    public static let claudeTab = "Claude"
    public static let launchAtLoginTitle = "Launch at login"
    public static let confirmRestartTitle = "Confirm before restarting Claude"
    public static let confirmQuitTitle = "Confirm before quitting"
    public static let notifyTitle = "Notify on external changes"
    public static let updatesHeader = "Updates"
    public static let autoUpdateTitle = "Automatically download and install updates"
    public static let checkForUpdatesTitle = "Check for Updates"
    public static let masterListHeader = "Master List Location"
    public static let chooseTitle = "Choose"
    public static let useDefaultTitle = "Use Default"
    public static let backupsHeader = "Backups"
    public static let backupsCaption = "Claude’s config, the master list and collections.json are backed up automatically before every change."
    public static let revealInFinderTitle = "Reveal in Finder"
    public static let claudeAppHeader = "Claude App"
    public static let claudeAppRejectedTitle = "That app can’t be used as Claude Desktop"
    public static let toolsHeader = ToolNote.settingsHeader
    public static let toolsCaption = ToolNote.settingsCaption
    /// "Claude Integration" is the Mac's title case; the Windows mirror says "Claude integration".
    public static let claudeIntegrationHeader = "Claude Integration"
    public static let claudeIntegrationTitle = "Let Claude manage Connector Control"
    public static let claudeIntegrationCaption = "Adds a connector named connector-control to Claude's config, so Claude can list, switch and edit your connectors. Takes effect when Claude restarts."
    public static let confirmClaudeChangesTitle = "Confirm changes Claude makes to connectors"
    public static let confirmClaudeChangesOnCaption = "Adding, editing or deleting a connector, deleting a collection, importing, subscribing, applying an update and restoring a backup ask here first."
    public static let confirmClaudeChangesOffCaption = "Claude can then change what it runs without asking here. Text Claude reads, such as a web page, could ask it to."
    /// "Command-Line Tool" is the Mac's title case; the Windows mirror says "Command-line tool".
    public static let commandLineToolHeader = "Command-Line Tool"
    /// How far a row's title fades while its switch cannot change, as the mockup draws it; the note that
    /// says why keeps full opacity.
    public static let dimmedOpacity = 0.45
    public static let loginItemApprovalNote = "Approve Connector Control under System Settings → General → Login Items."
    public static let minKeepCount = 5
    public static let maxKeepCount = 100

    public static func versionText(_ version: String) -> String { "Version \(version)" }

    public static func keepCountLabel(_ count: Int) -> String { "Keep \(count) backups of each file" }

    public static func loginItemFailureNote(_ message: String) -> String { "Couldn't update login item: \(message)" }

    private let state: AppState
    private let settings: AppSettings
    private let autostart: Autostart
    private let updater: Updater
    private let commandLineTool: CommandLineTool?
    private var subscriptions: Set<AnyCancellable> = []
    private var settingLaunchAtLoginSilently = false

    public init(state: AppState, settings: AppSettings, autostart: Autostart, updater: Updater,
                commandLineTool: CommandLineTool? = nil) {
        self.state = state
        self.settings = settings
        self.autostart = autostart
        self.updater = updater
        self.commandLineTool = commandLineTool
        launchAtLogin = autostart.isEnabled
        // Every change AppState announces repaints the view, as the popover's model relays them: the
        // tool statuses, and the Claude group's state, which the command-line tool, MDM, a synced
        // store or a failing endpoint can change while the window is open. Platform idiom: SwiftUI
        // repaints from this one relay; WPF bindings listen by name, so the Windows mirror raises the
        // members each change affects.
        state.objectWillChange
            .sink { [weak self] _ in self?.objectWillChange.send() }
            .store(in: &subscriptions)
        // A change made in Sparkle's own dialog must not leave the toggle stale.
        updater.automaticallyDownloadsUpdatesPublisher.dropFirst()
            .sink { [weak self] _ in self?.objectWillChange.send() }
            .store(in: &subscriptions)
    }

    /// Called whenever the General tab appears: autostart is read fresh (the
    /// user may have changed it in System Settings), and the updater's flag re-read.
    public func refresh() {
        setLaunchAtLoginSilently(autostart.isEnabled)
        objectWillChange.send()
    }

    // MARK: - General

    /// No-op when the OS already agrees; on failure revert the
    /// toggle and show the note; when approval is pending, say where.
    @Published public var launchAtLogin: Bool {
        didSet {
            if !settingLaunchAtLoginSilently, oldValue != launchAtLogin {
                applyLaunchAtLogin(launchAtLogin)
            }
        }
    }

    @Published public private(set) var loginItemNote: String?

    private func setLaunchAtLoginSilently(_ value: Bool) {
        settingLaunchAtLoginSilently = true
        launchAtLogin = value
        settingLaunchAtLoginSilently = false
    }

    private func applyLaunchAtLogin(_ wantOn: Bool) {
        let isOn = autostart.isEnabled
        guard wantOn != isOn else { return }
        do {
            try autostart.setEnabled(wantOn)
            loginItemNote = nil
        } catch {
            setLaunchAtLoginSilently(isOn)   // revert the checkbox
            loginItemNote = SettingsModel.loginItemFailureNote(error.localizedDescription)
        }
        if wantOn, autostart.requiresApproval {
            loginItemNote = SettingsModel.loginItemApprovalNote
        }
    }

    public var confirmBeforeRestart: Bool {
        get { settings.confirmBeforeRestart }
        set {
            objectWillChange.send()
            settings.confirmBeforeRestart = newValue
        }
    }

    public var confirmBeforeQuit: Bool {
        get { settings.confirmBeforeQuit }
        set {
            objectWillChange.send()
            settings.confirmBeforeQuit = newValue
        }
    }

    public var notifyExternalChanges: Bool {
        get { settings.notifyExternalChanges }
        set {
            objectWillChange.send()
            settings.notifyExternalChanges = newValue
        }
    }

    /// Mirrors Sparkle's own flag (it persists it itself).
    public var autoUpdate: Bool {
        get { updater.automaticallyDownloadsUpdates }
        set {
            guard updater.automaticallyDownloadsUpdates != newValue else { return }
            objectWillChange.send()
            updater.automaticallyDownloadsUpdates = newValue
        }
    }

    public var updatesEnabled: Bool { updater.isAvailable }

    public var versionText: String { SettingsModel.versionText(updater.versionDisplay) }

    public func checkForUpdates() { updater.checkForUpdates() }

    // MARK: - Storage

    public var storeDir: URL { state.service.paths.storeDirURL }

    public var canUseDefaultStore: Bool { !(settings.masterStoreDir ?? "").isEmpty }

    public func chooseStoreDir(_ dir: URL) {
        objectWillChange.send()
        state.repointStore(to: dir)
    }

    public func useDefaultStoreDir() {
        objectWillChange.send()
        state.repointStore(to: nil)
    }

    public var backupKeepCount: Int {
        get { settings.backupKeepCount }
        set {
            let clamped = min(max(newValue, SettingsModel.minKeepCount), SettingsModel.maxKeepCount)
            objectWillChange.send()   // the stepper's own binding snaps back to the clamped value
            guard clamped != settings.backupKeepCount else { return }
            settings.backupKeepCount = clamped
            state.refreshServiceSettings()
        }
    }

    public var keepCountLabel: String { SettingsModel.keepCountLabel(settings.backupKeepCount) }

    public func incrementKeepCount() { backupKeepCount = settings.backupKeepCount + 1 }

    public func decrementKeepCount() { backupKeepCount = settings.backupKeepCount - 1 }

    public var backupsDir: URL { state.service.backups.backupsDir }

    // MARK: - Claude

    public var claudeApp: URL { URL(fileURLWithPath: settings.claudeAppPath ?? AppState.defaultClaudeAppPath) }

    public var canUseDefaultClaudeApp: Bool { claudeApp.path != AppState.defaultClaudeAppPath }

    public func chooseClaudeApp(_ app: URL) {
        objectWillChange.send()
        settings.claudeAppPath = app.path
    }

    public func useDefaultClaudeApp() {
        objectWillChange.send()
        settings.claudeAppPath = nil
    }

    // MARK: - Claude integration

    /// The switch writes through AppState, which applies at once and answers the offer.
    public var claudeIntegrationEnabled: Bool {
        get { settings.claudeIntegrationEnabled }
        set {
            objectWillChange.send()
            state.setClaudeIntegrationEnabled(newValue)
        }
    }

    /// Dimmed while a connector holds the name, or the app runs from where it will not stay — unless
    /// the switch is on, so it can still go off.
    public var canChangeClaudeIntegration: Bool {
        settings.claudeIntegrationEnabled || (!state.claudeIntegrationClash && !state.controlToolIsTransient)
    }

    /// Why the switch is dimmed, under it: the app runs from where it will not stay (while off: moving
    /// it is the one thing that helps), else the clash; nil while there is neither.
    public var claudeIntegrationNote: String? {
        if !settings.claudeIntegrationEnabled, state.controlToolIsTransient { return ClaudeIntegration.moveToApplicationsError }
        return state.claudeIntegrationClash ? ClaudeIntegration.clashNote : nil
    }

    /// Under the switch: the note while there is one, else what the switch does.
    public var claudeIntegrationCaptionText: String { claudeIntegrationNote ?? SettingsModel.claudeIntegrationCaption }

    /// A note under the switch is a warning (amber in the view).
    public var claudeIntegrationCaptionIsWarning: Bool { claudeIntegrationNote != nil }

    /// The switch's title, dimmed with the switch; the note under it is not.
    public var claudeIntegrationTitleOpacity: Double { canChangeClaudeIntegration ? 1 : SettingsModel.dimmedOpacity }

    /// Why Claude and the command-line tool cannot reach this app, while they cannot; shown in the
    /// Claude Integration group whether the switch is on or off.
    public var controlEndpointProblem: String? { state.controlEndpointProblem }

    public var confirmClaudeChanges: Bool {
        get { settings.confirmClaudeChanges }
        set {
            objectWillChange.send()
            state.setConfirmClaudeChanges(newValue)
        }
    }

    /// The checkbox keeps its value while the switch is off, dimmed.
    public var canChangeConfirmClaudeChanges: Bool { settings.claudeIntegrationEnabled }

    /// Under the checkbox: what it guards while ticked, what is at stake while not, and nothing
    /// while the switch is off.
    public var confirmClaudeChangesCaption: String? {
        guard settings.claudeIntegrationEnabled else { return nil }
        return settings.confirmClaudeChanges ? SettingsModel.confirmClaudeChangesOnCaption : SettingsModel.confirmClaudeChangesOffCaption
    }

    /// The unticked caption is a warning (amber in the view).
    public var confirmClaudeChangesCaptionIsWarning: Bool {
        settings.claudeIntegrationEnabled && !settings.confirmClaudeChanges
    }

    /// The running app's version, as the Updates section shows it.
    public var version: String { updater.versionDisplay }

    // MARK: - Command-line tool

    /// Whether the app has a tool to put on PATH; the group is left out when it has none.
    public var hasCommandLineTool: Bool { commandLineTool != nil }

    /// Where the tool is, or that it is not installed; the link set apart.
    public var commandLineToolStatus: [TextRun] {
        guard let tool = commandLineTool else { return [] }
        guard tool.isInstalled else { return [TextRun(CommandLineToolText.notInstalledText)] }
        guard let location = tool.installedLocation else { return [TextRun(CommandLineToolText.inPathText)] }
        return TextRun.runs(CommandLineToolText.installedAtText(location), settingApart: [location])
    }

    /// What to do with it: run `help` once installed; else the full path scripts can always use.
    public var commandLineToolCaption: [TextRun] {
        guard let tool = commandLineTool else { return [] }
        if tool.isInstalled {
            return TextRun.runs(CommandLineToolText.runHelpCaption, settingApart: [CommandLineToolText.helpCommand])
        }
        return TextRun.runs(CommandLineToolText.scriptsCaption(tool.toolPath), settingApart: [tool.toolPath])
    }

    public var commandLineToolButton: String {
        commandLineTool?.isInstalled == true ? CommandLineToolText.removeButton : CommandLineToolText.installButton
    }

    /// Install or Remove is running: an administrator prompt, or the PATH's broadcast. The seam's state,
    /// so a window opened meanwhile sees it too.
    public var commandLineToolBusy: Bool { commandLineTool?.isBusy == true }

    @Published private var commandLineToolError: String?

    /// Under the row: why the last try failed, else why Install cannot work here; nil otherwise.
    public var commandLineToolMessage: String? {
        commandLineToolError ?? (installRefused ? ClaudeIntegration.moveToApplicationsError : nil)
    }

    public var canUseCommandLineToolButton: Bool { commandLineTool != nil && !commandLineToolBusy && !installRefused }

    /// Install while the app runs from where it will not stay: a link would point at a path about to be gone.
    private var installRefused: Bool { commandLineTool?.isInstalled == false && state.controlToolIsTransient }

    // Platform idiom: the Windows mirror raises the row's members by name as the operation starts and as it
    // ends; here objectWillChange repaints the view, which reads the seam's busy state afresh.
    /// The row's button: Install or Remove, one at a time. A failure stays under the row until the next try.
    public func commandLineToolAction() {
        guard let tool = commandLineTool, canUseCommandLineToolButton else { return }
        objectWillChange.send()
        commandLineToolError = nil
        let finished: (String?) -> Void = { [weak self] problem in
            self?.objectWillChange.send()
            self?.commandLineToolError = problem
        }
        if tool.isInstalled {
            tool.remove(completion: finished)
        } else {
            tool.install(completion: finished)
        }
    }

    // MARK: - Tools

    public var toolRows: [ToolRow] {
        Tool.allCases.map { ToolRow.make(tool: $0, status: state.toolStatuses[$0]) }
    }

    /// The Mac probes all four when the Claude tab appears.
    public func refreshTools() { state.refreshTools() }

    /// Stops listening to AppState and the updater. The app does not call
    /// this: the subscriptions hold `self` weakly and die with the
    /// `@StateObject`. Tests call it to prove the relays are what repaint
    /// the view.
    public func dispose() { subscriptions.removeAll() }
}

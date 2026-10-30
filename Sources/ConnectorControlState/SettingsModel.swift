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
    public static let startupHeader = "Startup"
    public static let launchAtLoginTitle = "Launch at login"
    public static let confirmationsHeader = "Confirmations"
    public static let confirmRestartTitle = "Confirm before restarting Claude"
    public static let confirmQuitTitle = "Confirm before quitting"
    public static let notificationsHeader = "Notifications"
    public static let notifyTitle = "Notify on external changes"
    public static let notifyCaption = "When the master list, a subscribed document or Claude’s config changes outside the app, and when Claude’s config is rebuilt from your list."
    public static let updatesHeader = "Updates"
    public static let autoUpdateTitle = "Automatically download and install updates"
    public static let checkForUpdatesTitle = "Check for Updates"
    public static let previewUpdatesCaption = "Previews update to later previews and the final release."
    public static let masterListHeader = "Master List Location"
    public static let masterListFooter = "Holds mcps.json and collections.json. A synced folder shares them across your Macs."
    /// The open panel's own button and title, which act at once, so no ellipsis.
    public static let chooseTitle = "Choose"
    /// The Settings button that opens the panel.
    public static let chooseButtonTitle = "Choose…"
    public static let useDefaultTitle = "Use Default"
    public static let backupsHeader = "Backups"
    public static let backupsTitle = "Backups of each file"
    public static let backupsCaption = "Claude’s config, the master list and collections.json."
    public static let backupsFooter = "A backup is made automatically before every change."
    public static let revealInFinderTitle = "Reveal in Finder"
    /// The Settings button that opens the restore sheet; the sheet's own button is RestoreModel.restoreTitle.
    public static let restoreButtonTitle = "Restore…"
    public static let claudeAppHeader = "Claude App"
    public static let claudeAppRejectedTitle = "That app can’t be used as Claude Desktop"
    public static let toolsHeader = ToolNote.settingsHeader
    public static let toolsCaption = ToolNote.settingsCaption
    public static let claudeIntegrationHeader = "Claude Integration"
    public static let claudeIntegrationTitle = "Let Claude manage Connector Control"
    public static let claudeIntegrationCaption = "Claude can list, switch and edit connectors. Restart Claude to apply."
    public static let confirmClaudeChangesTitle = "Confirm changes Claude makes"
    public static let confirmClaudeChangesOnCaption = "Ask before Claude adds, edits, deletes, imports or restores."
    public static let confirmClaudeChangesOffCaption = "Claude can then change what it runs without asking here. Text Claude reads, such as a web page, could ask it to."
    public static let commandLineToolHeader = "Command-Line Tool"
    /// How far a row's title fades while its switch cannot change, as the mockup draws it; the note that
    /// says why keeps full opacity.
    public static let dimmedOpacity = 0.45
    public static let loginItemApprovalNote = "Approve Connector Control under System Settings → General → Login Items."
    public static let minKeepCount = 5
    public static let maxKeepCount = 100

    public static func versionText(_ version: String) -> String { "Version \(version)" }

    public static func loginItemFailureNote(_ message: String) -> String { "Couldn't update login item: \(message)" }

    /// A folder's or file's own name, as a path row shows it on its first line.
    public static func shownName(_ path: String) -> String { (path as NSString).lastPathComponent }

    // Platform idiom: the Mac shortens a path in the home folder to ~, as Finder does; Windows shows it in full.
    /// The folder a path is in, as a path row's note shows it.
    public static func shownParent(_ path: String) -> String {
        ((path as NSString).deletingLastPathComponent as NSString).abbreviatingWithTildeInPath
    }

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
        // The command-line tool announces each Install or Remove as it starts and as it ends, so a window
        // opened meanwhile hears the end too, not only the one that started it.
        commandLineTool?.changes
            .sink { [weak self] _ in self?.objectWillChange.send() }
            .store(in: &subscriptions)
        // A setting changed from outside the window (the command line's `settings set`): launch at login,
        // the one value this model keeps rather than reads, is read again; the repaint the relay above
        // sends shows the rest.
        state.$settingsRevision.dropFirst()
            .sink { [weak self] _ in
                guard let self else { return }
                self.setLaunchAtLoginSilently(self.autostart.isEnabled)
            }
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

    /// The note is a problem (amber in the view): the login item could not be changed. Waiting for approval
    /// is an instruction, not a problem.
    public var loginItemNoteIsWarning: Bool { loginItemNote.map { $0 != SettingsModel.loginItemApprovalNote } ?? false }

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

    /// A preview build: its version carries a prerelease label, as 1.5.0-preview.10 does.
    public var isPreviewBuild: Bool { ControlVersion.parts(updater.versionDisplay)?.prerelease.isEmpty == false }

    /// Under the version, on a preview only: where a preview's updates come from.
    public var updatesCaption: String? { isPreviewBuild ? SettingsModel.previewUpdatesCaption : nil }

    public func checkForUpdates() { updater.checkForUpdates() }

    // MARK: - Storage

    public var storeDir: URL { state.service.paths.storeDirURL }

    public var storeDirName: String { SettingsModel.shownName(storeDir.path) }

    public var storeDirParent: String { SettingsModel.shownParent(storeDir.path) }

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

    public func incrementKeepCount() { backupKeepCount = settings.backupKeepCount + 1 }

    public func decrementKeepCount() { backupKeepCount = settings.backupKeepCount - 1 }

    public var backupsDir: URL { state.service.backups.backupsDir }

    public var backupsDirName: String { SettingsModel.shownName(backupsDir.path) }

    public var backupsDirParent: String { SettingsModel.shownParent(backupsDir.path) }

    // MARK: - Claude

    public var claudeApp: URL { URL(fileURLWithPath: settings.claudeAppPath ?? AppState.defaultClaudeAppPath) }

    /// macOS only: the app's name and folder, as its row shows them. Windows names a launch target instead.
    public var claudeAppName: String { SettingsModel.shownName(claudeApp.path) }

    public var claudeAppParent: String { SettingsModel.shownParent(claudeApp.path) }

    /// The row's note: the app's folder, or "Checking…" while a chosen app's signature is read.
    public var claudeAppNote: String { isCheckingClaudeApp ? ToolNote.checkingText : claudeAppParent }

    public var canUseDefaultClaudeApp: Bool { claudeApp.path != AppState.defaultClaudeAppPath }

    /// Whether a chosen app is being verified as Claude. The check reads the whole bundle, which
    /// takes seconds on a cold disk, so the row says so and Choose… waits rather than letting a
    /// second click start a second check beside the first.
    @Published public private(set) var isCheckingClaudeApp = false

    public var canChooseClaudeApp: Bool { !isCheckingClaudeApp }

    /// Starts a check, or answers false when one is already running and this choice is dropped.
    public func beginCheckingClaudeApp() -> Bool {
        guard !isCheckingClaudeApp else { return false }
        isCheckingClaudeApp = true
        return true
    }

    /// Ends the check, whatever it found: the view then refuses the app or calls `chooseClaudeApp`.
    public func endCheckingClaudeApp() { isCheckingClaudeApp = false }

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

    // Platform idiom: SwiftUI shows an optional directly; XAML cannot bind a visibility to one, so the
    // Windows mirror adds a Has… companion for each optional its views show or hide.

    public var confirmClaudeChanges: Bool {
        get { settings.confirmClaudeChanges }
        set {
            objectWillChange.send()
            state.setConfirmClaudeChanges(newValue)
        }
    }

    /// The second switch keeps its value while the first is off, dimmed.
    public var canChangeConfirmClaudeChanges: Bool { settings.claudeIntegrationEnabled }

    /// The second switch's title, dimmed with it; its note is hidden then.
    public var confirmClaudeChangesTitleOpacity: Double { canChangeConfirmClaudeChanges ? 1 : SettingsModel.dimmedOpacity }

    /// Under the second switch: what it guards while on, what is at stake while off, and nothing
    /// while the integration is off.
    public var confirmClaudeChangesCaption: String? {
        guard settings.claudeIntegrationEnabled else { return nil }
        return settings.confirmClaudeChanges ? SettingsModel.confirmClaudeChangesOnCaption : SettingsModel.confirmClaudeChangesOffCaption
    }

    /// The caption while it is off is a warning (amber in the view).
    public var confirmClaudeChangesCaptionIsWarning: Bool {
        settings.claudeIntegrationEnabled && !settings.confirmClaudeChanges
    }

    /// The running app's version, as the Updates section shows it.
    public var version: String { updater.versionDisplay }

    // MARK: - Command-line tool

    /// Whether the app has a tool to put on PATH; the group is left out when it has none.
    public var hasCommandLineTool: Bool { commandLineTool != nil }

    /// Where the tool is, or that it is not installed; the link's folder set apart.
    public var commandLineToolStatus: [TextRun] {
        guard let tool = commandLineTool else { return [] }
        guard tool.isInstalled else { return [TextRun(CommandLineToolText.notInstalledText)] }
        guard let location = tool.installedLocation else { return [TextRun(CommandLineToolText.inPathText)] }
        let folder = SettingsModel.shownParent(location)
        return TextRun.runs(CommandLineToolText.installedInText(folder), settingApart: [folder])
    }

    /// What to do with it: run `help` once installed; else the full path scripts can always use — left
    /// out while Install is refused, since that path is about to be gone.
    public var commandLineToolCaption: [TextRun] {
        guard let tool = commandLineTool, !installRefused else { return [] }
        if tool.isInstalled {
            return TextRun.runs(CommandLineToolText.runHelpCaption, settingApart: [CommandLineToolText.helpCommand])
        }
        return TextRun.runs(CommandLineToolText.scriptsCaption(tool.toolPath), settingApart: [tool.toolPath])
    }

    /// Whether the row has a caption: none while Install is refused, so the view leaves the line out rather
    /// than draw it empty.
    public var hasCommandLineToolCaption: Bool { !commandLineToolCaption.isEmpty }

    public var commandLineToolButton: String {
        commandLineTool?.isInstalled == true ? CommandLineToolText.removeButton : CommandLineToolText.installButton
    }

    /// Install or Remove is running: an administrator prompt, or the PATH's broadcast. The seam's state,
    /// so a window opened meanwhile sees it too.
    public var commandLineToolBusy: Bool { commandLineTool?.isBusy == true }

    /// Under the row: why the last try failed — the seam's, so a window opened meanwhile shows it — else
    /// why Install cannot work here; nil otherwise.
    public var commandLineToolMessage: String? {
        commandLineTool?.lastProblem ?? (installRefused ? ClaudeIntegration.moveToApplicationsError : nil)
    }

    public var canUseCommandLineToolButton: Bool { commandLineTool != nil && !commandLineToolBusy && !installRefused }

    /// Install while the app runs from where it will not stay: a link would point at a path about to be gone.
    private var installRefused: Bool { commandLineTool?.isInstalled == false && state.controlToolIsTransient }

    /// The row's button: Install or Remove, one at a time. A failure stays under the row until the next try.
    /// The seam keeps the busy state and the failure and announces both, so nothing waits on the completion.
    public func commandLineToolAction() {
        guard let tool = commandLineTool, canUseCommandLineToolButton else { return }
        if tool.isInstalled {
            tool.remove { _ in }
        } else {
            tool.install { _ in }
        }
    }

    // MARK: - Tools

    public var toolRows: [ToolRow] {
        Tool.allCases.map { ToolRow.make(tool: $0, status: state.toolStatuses[$0]) }
    }

    /// The Tools footer: one line for each family with a tool that has a problem, so every row stays one line tall.
    public var toolFooterLines: [ToolFooterLine] { ToolFooterLine.make(statuses: state.toolStatuses) }

    /// The Mac probes all four when the Claude tab appears.
    public func refreshTools() { state.refreshTools() }

    /// Stops listening to AppState, the updater and the command-line tool. The app does not call
    /// this: the subscriptions hold `self` weakly and die with the
    /// `@StateObject`. Tests call it to prove the relays are what repaint
    /// the view.
    public func dispose() { subscriptions.removeAll() }
}

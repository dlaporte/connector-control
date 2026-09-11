import Foundation
import Combine
import ConnectorControlCore

/// Catalog §4 SettingsView state: three tabs, every toggle, path, and button.
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
    public static let notifyTitle = "Notify about changes made outside Connector Control"
    public static let notifyCaption = "Covers edits to Claude's config and synced connector-list changes, including when a remote change needs a Claude restart."
    public static let updatesHeader = "Updates"
    public static let autoUpdateTitle = "Automatically download and install updates"
    public static let checkForUpdatesTitle = "Check for Updates…"
    public static let masterListHeader = "Master List Location"
    public static let chooseTitle = "Choose…"
    public static let useDefaultTitle = "Use Default"
    public static let backupsHeader = "Backups"
    public static let backupsCaption = "Both config files are backed up automatically before every change."
    public static let revealInFinderTitle = "Reveal in Finder"
    public static let restoreTitle = "Restore…"
    public static let claudeAppHeader = "Claude App"
    public static let claudeAppRejectedTitle = "That app can’t be used as Claude Desktop"
    public static let toolsHeader = ToolNote.settingsHeader
    public static let toolsCaption = ToolNote.settingsCaption
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
    private var subscriptions: Set<AnyCancellable> = []
    private var settingLaunchAtLoginSilently = false

    public init(state: AppState, settings: AppSettings, autostart: Autostart, updater: Updater) {
        self.state = state
        self.settings = settings
        self.autostart = autostart
        self.updater = updater
        launchAtLogin = autostart.isEnabled
        // Only the tool statuses are relayed from AppState. The Storage tab's
        // path and keep count derive from state.service, whose only writers
        // (repointStore, refreshServiceSettings) are called from this model,
        // which raises objectWillChange itself first. An AppState-side repoint
        // added later would need a relay of state.objectWillChange here.
        state.$toolStatuses.dropFirst()
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

    // MARK: - General (catalog §4.2)

    /// Catalog §4.2: no-op when the OS already agrees; on failure revert the
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

    // MARK: - Storage (catalog §4.3)

    public var storeDirPath: String { state.service.paths.storeDirURL.path }

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

    // MARK: - Claude (catalog §4.4)

    public var claudeAppPath: String { settings.claudeAppPath ?? AppState.defaultClaudeAppPath }

    public var canUseDefaultClaudeApp: Bool { claudeAppPath != AppState.defaultClaudeAppPath }

    public func chooseClaudeApp(_ app: URL) {
        objectWillChange.send()
        settings.claudeAppPath = app.path
    }

    public func useDefaultClaudeApp() {
        objectWillChange.send()
        settings.claudeAppPath = nil
    }

    // MARK: - Tools (spec 2026-09-05-tool-probe §3.5)

    public var toolRows: [ToolRow] {
        Tool.allCases.map { ToolRow.make(tool: $0, status: state.toolStatuses[$0]) }
    }

    /// Spec §6 D4: the Mac probes all four when the Claude tab appears.
    public func refreshTools() { state.refreshTools() }

    /// Stops listening to AppState and the updater. The app does not call
    /// this: the subscriptions hold `self` weakly and die with the
    /// `@StateObject`. Tests call it to prove the relays are what repaint
    /// the view.
    public func dispose() { subscriptions.removeAll() }
}

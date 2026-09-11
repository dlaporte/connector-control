import Foundation
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// A real on-disk layout (a throwaway ~/Library/Application Support), the real
/// path rule, real ConfigService and FileWatchers, and fakes for the platform
/// seams only. Marshal is a queue: watcher callbacks and probe results reach
/// state only when a test pumps, mirroring the main actor in the app.
@MainActor
final class AppStateHarness {
    struct HarnessError: Error {}

    let dir = TempDir(prefix: "appstate")
    var appSupport: URL { dir.file("Library/Application Support") }
    var claudeConfigURL: URL { appSupport.appendingPathComponent("Claude/claude_desktop_config.json") }
    var storeDir: URL { appSupport.appendingPathComponent("Connector Control") }
    var masterStoreURL: URL { storeDir.appendingPathComponent("mcps.json") }
    var backupsDir: URL { storeDir.appendingPathComponent("backups") }
    var stagingDir: URL { storeDir.appendingPathComponent(".staging") }

    let settings = FakeSettings()
    let claude = FakeClaudeProcess()
    let notifier = FakeNotifier()
    let dialogs = FakeDialogs()
    let tools = FakeToolProbe()
    let delays = DelayQueue()
    let ui = MarshalQueue()
    /// The clock every lastApplyDate is stamped with; tests move it.
    var now = ISO8601DateFormatter().date(from: "2026-09-04T12:00:00Z")!
    private var created: [AppState] = []

    var context: PathContext { PathContext(environment: [:], appSupport: appSupport) }

    var host: AppHost {
        AppHost(marshal: { [ui] in ui.post($0) }, delay: { [delays] in delays.add($0, $1) },
                now: { [unowned self] in MainActor.assumeIsolated { self.now } })
    }

    /// `createClaudeDirectory: false` leaves even the Claude folder absent, so the
    /// Claude-config watcher cannot arm at launch (the re-arm test).
    init(seedClaudeConfig: Bool = true, createClaudeDirectory: Bool = true) {
        let fm = FileManager.default
        try! fm.createDirectory(at: appSupport, withIntermediateDirectories: true)
        if createClaudeDirectory {
            try! fm.createDirectory(at: claudeConfigURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        }
        if seedClaudeConfig {
            try! Data(Fixtures.realisticClaudeConfig.utf8).write(to: claudeConfigURL)
        }
    }

    func create() -> AppState {
        let state = AppState(settings: settings, claude: claude, notifier: notifier, dialogs: dialogs,
                             paths: context, host: host, toolProbe: tools)
        created.append(state)
        return state
    }

    func claudeServers() throws -> [String: JSONValue] {
        try ClaudeConfigIO.readMCPServers(at: claudeConfigURL)
    }

    func storeOnDisk() throws -> MasterStore {
        guard let store = MasterStoreIO.read(from: masterStoreURL) else { throw HarnessError() }
        return store
    }

    /// An "external" edit of Claude's config: replaces mcpServers, keeps every other key.
    func writeClaudeServers(_ servers: [(String, JSONValue)]) throws {
        try ClaudeConfigIO.write(mcpServers: Dictionary(uniqueKeysWithValues: servers), to: claudeConfigURL)
    }

    static func remote(_ url: String) -> JSONValue {
        RemotePattern.make(url: url)
    }

    /// Disposes every AppState this harness created (stops their watchers) and deletes the temp dir.
    func dispose() {
        created.forEach { $0.dispose() }
        created.removeAll()
        dir.dispose()
    }
}

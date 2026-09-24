import Foundation
import XCTest
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
    private var models: [CollectionsModel] = []

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

    /// A local connector running `command` with `args`.
    static func localConnector(_ command: String, _ args: [String] = []) -> MCPEntry {
        MCPEntry(config: .object(["command": .string(command), "args": .array(args.map(JSONValue.string))]))
    }

    /// Claude running since `hours` before the harness clock, so the next apply calls for a restart.
    func claudeRunningSince(hours: Double) {
        claude.isRunning = true
        claude.launchDate = now.addingTimeInterval(-hours * 3600)
    }

    // MARK: Collections

    /// The bytes an author's machine would have written, at `name` under the temp dir.
    @discardableResult
    func writeDocument(_ doc: CollectionDocument, named name: String) throws -> URL {
        let url = dir.file(name)
        try writeDocument(doc, at: url)
        return url
    }

    /// The same, at a path the test already holds: the author's next commit to a document.
    func writeDocument(_ doc: CollectionDocument, at url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try doc.serialized().write(to: url)
    }

    /// Writes `doc` at `name` under the temp dir and subscribes `state` to it, as `collection` or
    /// under the document's own name; a refusal fails the test. Returns where the document is.
    @discardableResult
    func subscribe(_ state: AppState, to doc: CollectionDocument, at name: String = "data-team.json",
                   as collection: String? = nil, file: StaticString = #filePath, line: UInt = #line) throws -> URL {
        let url = try writeDocument(doc, named: name)
        XCTAssertNil(state.subscribe(documentAt: url.path, as: collection), file: file, line: line)
        return url
    }

    /// Writes both collection files where the app reads them, then reloads so the state picks
    /// them up — the shape a subscribe or a publish would leave behind.
    func seed(_ state: AppState, file: CollectionsFile, cache: CollectionsLocalCache? = nil) throws {
        try file.save(to: storeDir.appendingPathComponent(CollectionsFile.fileName), staging: nil)
        try (cache ?? CollectionsLocalCache(synced: [:], published: [:]))
            .save(to: state.service.paths.collectionsCacheURL, staging: nil)
        state.reload()
    }

    /// Marks `name`, already in the store, synced from `<slug>.json`: found at `path` on this
    /// machine or, with nil, not found here. It is the only collection the sidecar describes and
    /// the only synced binding in the cache; the rest of the cache is kept.
    func makeSynced(_ state: AppState, _ name: String, boundTo path: String? = nil) throws {
        let entry = CollectionsFile.Entry(kind: .synced, fileName: Slug.make(name) + ".json")
        var cache = state.collectionsCache
        cache.synced = path.map { [name: CollectionsLocalCache.SyncedBinding(path: $0, lastHash: nil, excluded: [:])] } ?? [:]
        try seed(state, file: CollectionsFile(collections: [name: entry]), cache: cache)
    }

    /// Publishes `collection` into a new folder `folder` under the temp dir; a refusal fails the
    /// test. Returns the document it wrote.
    @discardableResult
    func publish(_ state: AppState, _ collection: String, intent: PublishIntent = .none, folder: String = "pub",
                 file: StaticString = #filePath, line: UInt = #line) throws -> URL {
        let url = dir.file(folder)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        XCTAssertNil(state.startPublishing(collection, to: url.path, intent: intent), file: file, line: line)
        return url.appendingPathComponent(CollectionDocument.fileName(slug: Slug.make(collection)))
    }

    /// Reads the store off disk, lets `edit` change it and saves it back: a write from another
    /// machine or an older app, which the running state has not seen.
    func editStoreOnDisk(_ edit: (inout MasterStore) throws -> Void) throws {
        var store = try storeOnDisk()
        try edit(&store)
        try MasterStoreIO.save(store, to: masterStoreURL)
    }

    /// A Collections window's model on `state`, showing `selecting` (the active collection when
    /// nil) with `ticking` ticked. The harness disposes it.
    func collectionsModel(_ state: AppState, selecting: String? = nil, ticking: [String] = []) -> CollectionsModel {
        let model = CollectionsModel(state: state, dialogs: dialogs)
        models.append(model)
        if let selecting { model.selected = selecting }
        for name in ticking { model.setChecked(name, true) }
        return model
    }

    /// Disposes every model and AppState this harness created (stops their watchers) and deletes
    /// the temp dir.
    func dispose() {
        models.forEach { $0.dispose() }
        models.removeAll()
        created.forEach { $0.dispose() }
        created.removeAll()
        dir.dispose()
    }

    /// The harness plus its AppState, in one call — the shape most tests
    /// need. `let (h, state) = AppStateHarness.started(); defer { h.dispose() }`.
    /// A test that must act on the harness BEFORE the AppState exists (seed a
    /// fake's state, change a setting) still uses `AppStateHarness()` +
    /// `h.create()` directly.
    static func started(seedClaudeConfig: Bool = true, createClaudeDirectory: Bool = true)
        -> (AppStateHarness, AppState) {
        let h = AppStateHarness(seedClaudeConfig: seedClaudeConfig, createClaudeDirectory: createClaudeDirectory)
        return (h, h.create())
    }

    /// Advances the mtime of Claude's config and the master store (whichever
    /// currently exist) past whatever they currently are — called after an
    /// action (direct or through AppState) that must be observably distinct,
    /// to the watchers, from whatever they last saw at arm time. Replaces
    /// `Thread.sleep` used only to separate two writes' mtimes by real
    /// wall-clock time.
    func touchWatchedFiles() throws {
        let fm = FileManager.default
        if fm.fileExists(atPath: claudeConfigURL.path) {
            try TempDir.bumpModificationDate(of: claudeConfigURL)
        }
        if fm.fileExists(atPath: masterStoreURL.path) {
            try TempDir.bumpModificationDate(of: masterStoreURL)
        }
    }
}

import XCTest
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// Catalog §6.2 on the marshalled watcher: the callback is posted, never
/// delivered until the test pumps, and dropped after a stop or restart. A
/// truncate+write can post two callbacks before one pump, so hit counts are
/// lower bounds, as the C# suite asserts them.
@MainActor
final class FileWatcherTests: XCTestCase {
    private let wait: TimeInterval = 5
    private let settle: TimeInterval = 0.7

    @MainActor
    private final class Rig {
        let dir = TempDir(prefix: "watch")
        let ui = MarshalQueue()
        var hits = 0
        var file: URL { dir.file("watched.json") }
        var watcher: FileWatcher?

        @MainActor
        func make(_ url: URL? = nil) -> FileWatcher {
            let w = FileWatcher(url: url ?? file, marshal: { [ui] in ui.post($0) }) { [unowned self] in self.hits += 1 }
            watcher = w
            return w
        }

        /// Waits for the watcher's queue to post something WITHOUT running it.
        func waitForPost(timeout: TimeInterval) -> Bool {
            let deadline = Date().addingTimeInterval(timeout)
            while ui.pending == 0 {
                if Date() >= deadline { return false }
                Thread.sleep(forTimeInterval: 0.05)
            }
            return true
        }

        func dispose() {
            watcher?.stop()
            dir.dispose()
        }
    }

    func testFiresOnInPlaceWrite() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        r.make().start()
        Thread.sleep(forTimeInterval: 0.3)
        try Data("bb".utf8).write(to: r.file)   // no .atomic: truncate + write in place
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait))
    }

    func testFiresOnAtomicReplace() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        r.make().start()
        Thread.sleep(forTimeInterval: 0.3)
        try Data("bb".utf8).write(to: r.file, options: .atomic)   // a new inode under the same name
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait))
    }

    func testFiresOnDeleteAndOnRecreate() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        r.make().start()
        Thread.sleep(forTimeInterval: 0.3)
        try FileManager.default.removeItem(at: r.file)
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait))
        Thread.sleep(forTimeInterval: 0.3)
        try Data("back".utf8).write(to: r.file)
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 2 }, timeout: wait))
    }

    func testDoesNotFireAfterStop() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        let watcher = r.make()
        watcher.start()
        XCTAssertTrue(watcher.isArmed)
        watcher.stop()
        XCTAssertFalse(watcher.isArmed)
        Thread.sleep(forTimeInterval: 0.3)
        try Data("bb".utf8).write(to: r.file)
        XCTAssertFalse(r.ui.pumpUntil({ r.hits > 0 }, timeout: settle))
        XCTAssertEqual(r.ui.pending, 0)
    }

    func testRestartDropsCallbacksScheduledBeforeTheRestart() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        let watcher = r.make()
        watcher.start()
        Thread.sleep(forTimeInterval: 0.3)
        try Data("bb".utf8).write(to: r.file)
        XCTAssertTrue(r.waitForPost(timeout: wait))   // posted, not yet delivered
        watcher.stop()
        watcher.start()
        r.ui.pump()
        XCTAssertEqual(r.hits, 0, "a callback from the previous generation is dropped")
        Thread.sleep(forTimeInterval: 0.3)
        try Data("ccc".utf8).write(to: r.file)
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait), "the restarted watcher is live")
    }

    func testIgnoresOtherFilesInTheDirectory() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        r.make().start()
        Thread.sleep(forTimeInterval: 0.3)
        try Data("noise".utf8).write(to: r.dir.file("other.json"))
        XCTAssertFalse(r.ui.pumpUntil({ r.hits > 0 }, timeout: settle))
    }

    func testDoesNotFireWhenStoppedWhileAnEventIsInFlight() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        let watcher = r.make()
        watcher.start()
        Thread.sleep(forTimeInterval: 0.3)
        try Data("bb".utf8).write(to: r.file)
        XCTAssertTrue(r.waitForPost(timeout: wait))   // posted, not yet delivered
        watcher.stop()                                // before the posted callback runs
        r.ui.pump()
        XCTAssertEqual(r.hits, 0, "a callback scheduled before the stop is dropped on delivery")
    }

    func testCallbackGoesThroughMarshal() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        r.make().start()
        Thread.sleep(forTimeInterval: 0.3)
        try Data("bb".utf8).write(to: r.file)
        XCTAssertTrue(r.waitForPost(timeout: wait), "the change is posted to marshal, not delivered inline")
        XCTAssertEqual(r.hits, 0, "not delivered until the test pumps marshal's queue")
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait))
    }

    /// The deletion of the watched directory itself (not just the file) is
    /// delivered exactly once and disarms the watcher, however many of the
    /// directory's and the file's own DispatchSource events fire for it — the
    /// serial queue plus the top-of-function guard collapse them to one.
    func testADeletedDirectoryFiresOnceAndDisarms() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        let watcher = r.make()
        watcher.start()
        XCTAssertTrue(watcher.isArmed)
        try FileManager.default.removeItem(at: r.dir.url)
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait))
        XCTAssertFalse(watcher.isArmed)
        _ = r.ui.pumpUntil({ false }, timeout: settle)   // give a possible second event a chance to (mis)fire
        XCTAssertEqual(r.hits, 1)
    }

    func testADeletedDirectoryDisarmsTheWatcherSoTheNextStartReArms() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        let watcher = r.make()
        watcher.start()
        try FileManager.default.removeItem(at: r.dir.url)
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait))
        XCTAssertFalse(watcher.isArmed)
        watcher.start()   // AppState retries on every reload; the directory is still gone
        XCTAssertFalse(watcher.isArmed)
    }

    func testStartAfterTheDirectoryReappearsWatchesAgain() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        let watcher = r.make()
        watcher.start()
        try FileManager.default.removeItem(at: r.dir.url)
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait))
        try FileManager.default.createDirectory(at: r.dir.url, withIntermediateDirectories: true)
        watcher.start()
        XCTAssertTrue(watcher.isArmed)
        Thread.sleep(forTimeInterval: 0.3)
        try Data("back".utf8).write(to: r.file)
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 2 }, timeout: wait), "the re-armed watcher still reports changes")
    }

    func testStaysUnarmedUntilTheParentDirectoryExists() throws {
        let r = Rig()
        defer { r.dispose() }
        let later = r.dir.file("later/watched.json")
        let watcher = r.make(later)
        watcher.start()
        XCTAssertFalse(watcher.isArmed, "no parent directory: nothing to open")
        try FileManager.default.createDirectory(at: later.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("a".utf8).write(to: later)
        watcher.start()
        XCTAssertTrue(watcher.isArmed)
        watcher.start()   // a no-op while armed
        Thread.sleep(forTimeInterval: 0.3)
        try Data("bb".utf8).write(to: later)
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait))
    }
}

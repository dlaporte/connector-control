import XCTest
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// The marshalled watcher: the callback is posted, never
/// delivered until the test pumps, and dropped after a stop or restart. A
/// truncate+write can post two callbacks before one pump, so hit counts are
/// lower bounds, as the C# suite asserts them. `TempDir.touch`/
/// `bumpModificationDate` give a triggering write an mtime distinct from
/// whatever the watcher saw at arm time, deterministically — no sleep needed.
@MainActor
final class FileWatcherTests: XCTestCase {
    private let wait: TimeInterval = 5
    /// A window to prove an event does NOT arrive (a stopped watcher, an
    /// unrelated file) — unlike `wait`, timing out here is the pass case, so
    /// it cannot be replaced by `pumpUntil` waiting on a condition.
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
        try TempDir.touch(r.file, "bb")   // no .atomic: truncate + write in place
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait))
    }

    func testFiresOnAtomicReplace() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        r.make().start()
        try Data("bb".utf8).write(to: r.file, options: .atomic)   // a new inode under the same name
        try TempDir.bumpModificationDate(of: r.file)
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait))
    }

    func testFiresOnDeleteAndOnRecreate() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        r.make().start()
        try FileManager.default.removeItem(at: r.file)   // a deletion's mtime (nil) always differs: no separator needed
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait))
        try TempDir.touch(r.file, "back")
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
        try Data("bb".utf8).write(to: r.file)   // stopped: no comparison happens, so timing cannot matter
        XCTAssertFalse(r.ui.pumpUntil({ r.hits > 0 }, timeout: settle))
        XCTAssertEqual(r.ui.pending, 0)
    }

    func testRestartDropsCallbacksScheduledBeforeTheRestart() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        let watcher = r.make()
        watcher.start()
        try TempDir.touch(r.file, "bb")
        XCTAssertTrue(r.waitForPost(timeout: wait))   // posted, not yet delivered
        watcher.stop()
        watcher.start()
        r.ui.pump()
        XCTAssertEqual(r.hits, 0, "a callback from the previous generation is dropped")
        try TempDir.touch(r.file, "ccc")
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait), "the restarted watcher is live")
    }

    func testIgnoresOtherFilesInTheDirectory() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        r.make().start()
        try TempDir.touch(r.dir.file("other.json"), "noise")
        XCTAssertFalse(r.ui.pumpUntil({ r.hits > 0 }, timeout: settle))
    }

    func testDoesNotFireWhenStoppedWhileAnEventIsInFlight() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        let watcher = r.make()
        watcher.start()
        try TempDir.touch(r.file, "bb")
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
        try TempDir.touch(r.file, "bb")
        XCTAssertTrue(r.waitForPost(timeout: wait), "the change is posted to marshal, not delivered inline")
        XCTAssertEqual(r.hits, 0, "not delivered until the test pumps marshal's queue")
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait))
    }

    /// Removing the watched directory is two separate disappearances on disk —
    /// the file is unlinked, then the directory itself — and the watcher
    /// reports each one, so the tests below take them one at a time and wait
    /// for the file's own change to be delivered before the directory goes.
    /// Collapsing them into a single `removeItem(at: dir)` makes the watcher's
    /// state at any given hit count a race: the first callback is whichever
    /// disappearance the kernel reported first, and only the directory's
    /// disarms. `testRemovingTheDirectoryWithTheFileStillInItReportsAndDisarms`
    /// keeps that real-world shape, asserting only what holds either way.
    ///
    /// The directory's own deletion is delivered exactly once and disarms the
    /// watcher, however many of the directory's and the file's DispatchSource
    /// events fire for it — the serial queue plus the top-of-function guard
    /// collapse them to one.
    func testADeletedDirectoryFiresOnceAndDisarms() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        let watcher = r.make()
        watcher.start()
        XCTAssertTrue(watcher.isArmed)
        try FileManager.default.removeItem(at: r.file)
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait), "the file's own disappearance")
        try FileManager.default.removeItem(at: r.dir.url)
        XCTAssertTrue(r.ui.pumpUntil({ !watcher.isArmed }, timeout: wait), "the directory's deletion disarms")
        _ = r.ui.pumpUntil({ false }, timeout: settle)   // give a possible second event a chance to (mis)fire
        XCTAssertEqual(r.hits, 2, "one callback for the file, exactly one for the directory")
    }

    /// The shape the app actually meets: the directory goes with the file still
    /// in it. Which disappearance the watcher sees first is the kernel's
    /// choice, so only the end state is asserted — the change is reported and
    /// the watcher ends up disarmed.
    func testRemovingTheDirectoryWithTheFileStillInItReportsAndDisarms() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        let watcher = r.make()
        watcher.start()
        try FileManager.default.removeItem(at: r.dir.url)
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 && !watcher.isArmed }, timeout: wait))
    }

    func testADeletedDirectoryDisarmsTheWatcherSoTheNextStartReArms() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        let watcher = r.make()
        watcher.start()
        try FileManager.default.removeItem(at: r.file)
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait), "the file's own disappearance")
        try FileManager.default.removeItem(at: r.dir.url)
        XCTAssertTrue(r.ui.pumpUntil({ !watcher.isArmed }, timeout: wait), "the directory's deletion disarms")
        watcher.start()   // AppState retries on every reload; the directory is still gone
        XCTAssertFalse(watcher.isArmed)
    }

    func testStartAfterTheDirectoryReappearsWatchesAgain() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        let watcher = r.make()
        watcher.start()
        try FileManager.default.removeItem(at: r.file)
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait), "the file's own disappearance")
        try FileManager.default.removeItem(at: r.dir.url)
        // The disarm has to be observed before the directory comes back: a
        // watcher still armed on the deleted directory's descriptor treats the
        // next start() as a no-op and never sees the new one's contents.
        XCTAssertTrue(r.ui.pumpUntil({ !watcher.isArmed }, timeout: wait), "the directory's deletion disarms")
        try FileManager.default.createDirectory(at: r.dir.url, withIntermediateDirectories: true)
        watcher.start()
        XCTAssertTrue(watcher.isArmed)
        try TempDir.touch(r.file, "back")
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 3 }, timeout: wait), "the re-armed watcher still reports changes")
    }

    /// A directory replaced wholesale: a sync client swapping a folder in, or a
    /// delete and recreate that the watcher never sees as a gap. `RENAME_SWAP`
    /// makes the replacement atomic, which is the only way a test can produce
    /// it — delete-then-recreate leaves a window in which the watcher may
    /// instead see the path missing and take its deleted-directory branch. Here
    /// the path never stops existing, so the one thing that changed is which
    /// directory it resolves to, and the descriptor the watcher holds is
    /// orphaned: nothing happening under the path can reach it again.
    ///
    /// The replacement is empty, so the write at the end CREATES the watched
    /// file. Only the directory source can see a create — the file source has
    /// no descriptor to arm on a file that does not exist yet — which is what
    /// makes that assertion fail for a watcher still on the old directory.
    func testAReplacedDirectoryIsFollowedOnTheNextStart() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        let watcher = r.make()
        watcher.start()

        let replacement = TempDir(prefix: "watch-replacement")
        defer { replacement.dispose() }
        XCTAssertEqual(renamex_np(replacement.url.path, r.dir.url.path, UInt32(RENAME_SWAP)), 0,
                       "the atomic swap has to succeed for the rest of this test to mean anything")
        XCTAssertFalse(watcher.isArmed, "the descriptor is no longer the directory at the path")
        watcher.start()   // AppState re-arms on every reload: this is where recovery happens
        XCTAssertTrue(watcher.isArmed)
        // The swap took the watched file out of the path, and the re-arm's own
        // re-check reports that whether or not an event ever arrives for the
        // swap. Exactly one callback either way: whichever of the two gets
        // there first leaves the last-seen date at "no file", and the other
        // then finds nothing changed. So the count below can only be the write.
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait),
                      "the swap itself is reported: the watched file is not in the folder that is there now")
        try TempDir.touch(r.file, "back")
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 2 }, timeout: wait),
                      "the watcher follows the path, not the descriptor it happened to open")
    }

    /// The replacement no event can report: a sync client swapping the folder
    /// ABOVE the watched one. Nothing touches the watched directory's own inode
    /// or the file's — only what the path resolves to changes — so no kqueue
    /// event is delivered for either, and the re-arm's own re-check is the only
    /// thing that can notice the difference. This is the shape a synced
    /// collection meets when the shared folder holding it is replaced.
    func testAReplacedGrandparentIsReCheckedByTheReArm() throws {
        let r = Rig()
        defer { r.dispose() }
        let team = r.dir.file("team")
        let file = team.appendingPathComponent("collection/watched.json")
        try TempDir.touch(file, "a")
        let watcher = r.make(file)
        watcher.start()
        XCTAssertTrue(watcher.isArmed)

        // A whole tree swapped in atomically, holding an empty collection
        // folder: the watched file is not in it, and the watcher's descriptor
        // is on the collection folder of the tree that was swapped away.
        let replacement = TempDir(prefix: "watch-team")
        defer { replacement.dispose() }
        try FileManager.default.createDirectory(
            at: replacement.file("collection"), withIntermediateDirectories: true)
        XCTAssertEqual(renamex_np(replacement.url.path, team.path, UInt32(RENAME_SWAP)), 0)

        XCTAssertFalse(watcher.isArmed, "the descriptor is no longer the directory at the path")
        _ = r.ui.pumpUntil({ false }, timeout: settle)
        XCTAssertEqual(r.hits, 0, "no inode the watcher holds was touched, so no event can fire")
        watcher.start()   // AppState re-arms on every reload
        XCTAssertTrue(watcher.isArmed)
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait),
                      "the re-arm has to re-check: nothing else will ever report this change")
    }

    /// A re-arm that cannot open the replacement has to leave the watcher on
    /// the directory it already has. Giving the live source up for an open that
    /// then fails would disarm it silently, and the next start() would come in
    /// cold and re-baseline the last-seen date, swallowing whatever change was
    /// waiting. Swapping the original directory back is the proof: the
    /// descriptor the watcher kept is the directory at the path again, so it
    /// reports itself armed on it without another start(), which a watcher that
    /// had thrown that descriptor away cannot do.
    func testAReArmThatCannotOpenTheReplacementKeepsTheLiveSource() throws {
        let r = Rig()
        defer { r.dispose() }
        try Data("a".utf8).write(to: r.file)
        let watcher = r.make()
        watcher.start()

        // A directory the watcher is not permitted to open, so the re-arm's
        // open() fails while the path still resolves to a directory.
        let unopenable = TempDir(prefix: "watch-unopenable")
        defer { _ = chmod(unopenable.url.path, 0o755); unopenable.dispose() }
        XCTAssertEqual(chmod(unopenable.url.path, 0), 0)
        XCTAssertEqual(renamex_np(unopenable.url.path, r.dir.url.path, UInt32(RENAME_SWAP)), 0)
        XCTAssertFalse(watcher.isArmed, "the descriptor is no longer the directory at the path")
        watcher.start()
        XCTAssertFalse(watcher.isArmed, "and the replacement could not be opened")

        XCTAssertEqual(renamex_np(unopenable.url.path, r.dir.url.path, UInt32(RENAME_SWAP)), 0)
        XCTAssertTrue(watcher.isArmed, "the failed re-arm must not have thrown the live source away")
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
        try TempDir.touch(later, "bb")
        XCTAssertTrue(r.ui.pumpUntil({ r.hits >= 1 }, timeout: wait))
    }
}

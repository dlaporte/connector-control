import XCTest
@testable import ConnectorControlCore

final class AtomicFileTests: XCTestCase {
    var dir: URL!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("atomic-\(UUID().uuidString)")
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
        AtomicFile.privateStagingDirectory = nil
    }

    func testWriteCreatesFileAndIntermediateDirectories() throws {
        let url = dir.appendingPathComponent("nested/file.json")
        try AtomicFile.write(Data("hello".utf8), to: url)
        XCTAssertEqual(try String(contentsOf: url, encoding: .utf8), "hello")
    }

    func testWriteReplacesExistingFile() throws {
        let url = dir.appendingPathComponent("file.json")
        try AtomicFile.write(Data("one".utf8), to: url)
        try AtomicFile.write(Data("two".utf8), to: url)
        XCTAssertEqual(try String(contentsOf: url, encoding: .utf8), "two")
    }

    func testWritesArePrivate() throws {
        let url = dir.appendingPathComponent("secret.json")
        try AtomicFile.write(Data("token".utf8), to: url)
        let mode = try XCTUnwrap(FileManager.default
            .attributesOfItem(atPath: url.path)[.posixPermissions] as? Int)
        XCTAssertEqual(mode, 0o600)
    }

    /// Claude's own config file is created by Claude Desktop with the default
    /// umask (0644). Every write over it must end owner-only, not inherit the
    /// old mode — that is the whole point of the 0600 set on the temp file.
    func testWriteOverExistingWorldReadableFileIsPrivate() throws {
        let fm = FileManager.default
        let url = dir.appendingPathComponent("claude_desktop_config.json")
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        try Data("old".utf8).write(to: url)
        try fm.setAttributes([.posixPermissions: 0o644], ofItemAtPath: url.path)
        try AtomicFile.write(Data("new".utf8), to: url)
        XCTAssertEqual(try String(contentsOf: url, encoding: .utf8), "new")
        let mode = try XCTUnwrap(fm.attributesOfItem(atPath: url.path)[.posixPermissions] as? Int)
        XCTAssertEqual(mode, 0o600)
    }

    /// A fresh install: the store directory does not exist until the first
    /// save creates it, and it must be private from that moment, not from the
    /// next launch's sweep.
    func testCreatedDirectoriesArePrivate() throws {
        let fm = FileManager.default
        let url = dir.appendingPathComponent("nested/mcps.json")
        try AtomicFile.write(Data("{}".utf8), to: url)
        for directory in [dir!, dir.appendingPathComponent("nested")] {
            let mode = try XCTUnwrap(fm.attributesOfItem(atPath: directory.path)[.posixPermissions] as? Int)
            XCTAssertEqual(mode, 0o700, directory.lastPathComponent)
        }
    }

    /// The mode comes from open(2) at creation, not from a chmod after the
    /// bytes are on disk: with the umask cleared, Data.write would create
    /// 0666 and the secrets would be world-readable until the chmod landed.
    func testWriteIsPrivateFromCreationRegardlessOfUmask() throws {
        let previous = umask(0)
        defer { umask(previous) }
        let url = dir.appendingPathComponent("secret.json")
        try AtomicFile.write(Data("token".utf8), to: url)
        let mode = try XCTUnwrap(FileManager.default
            .attributesOfItem(atPath: url.path)[.posixPermissions] as? Int)
        XCTAssertEqual(mode, 0o600)
    }

    /// A config symlinked into a dotfiles repo is written through: the link
    /// survives, the real file gets the bytes and the private mode.
    func testWriteThroughASymlinkUpdatesTheTargetAndKeepsTheLink() throws {
        let fm = FileManager.default
        let real = dir.appendingPathComponent("dotfiles/claude.json")
        let link = dir.appendingPathComponent("claude_desktop_config.json")
        try fm.createDirectory(at: real.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("old".utf8).write(to: real)
        try fm.setAttributes([.posixPermissions: 0o644], ofItemAtPath: real.path)
        try fm.createSymbolicLink(at: link, withDestinationURL: real)
        try AtomicFile.write(Data("new".utf8), to: link)
        XCTAssertEqual(try fm.destinationOfSymbolicLink(atPath: link.path), real.path, "the link is still a link")
        XCTAssertEqual(try String(contentsOf: real, encoding: .utf8), "new")
        let mode = try XCTUnwrap(fm.attributesOfItem(atPath: real.path)[.posixPermissions] as? Int)
        XCTAssertEqual(mode, 0o600)
    }

    func testNoTempFilesLeftBehind() throws {
        let url = dir.appendingPathComponent("file.json")
        try AtomicFile.write(Data("x".utf8), to: url)
        let names = try FileManager.default.contentsOfDirectory(atPath: dir.path)
        XCTAssertEqual(names, ["file.json"])
    }

    func testNoTempFilesLeftBehindOnFailure() throws {
        let fm = FileManager.default

        // Create test directory structure
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)

        // Test case: create a file where we need a directory, causing createDirectory to fail
        let blockingPath = dir.appendingPathComponent("blocking")
        try Data("placeholder".utf8).write(to: blockingPath)

        let url = dir.appendingPathComponent("blocking/file.json")

        // This should fail at createDirectory before tmp is created
        var didThrow = false
        do {
            try AtomicFile.write(Data("test".utf8), to: url)
        } catch {
            didThrow = true
        }
        XCTAssert(didThrow, "Expected write to throw but it succeeded")

        // Verify no .tmp- files left behind (there shouldn't be any because tmp was never created)
        let parentContents = try fm.contentsOfDirectory(atPath: dir.path)
        let tmpFiles = parentContents.filter { $0.contains(".tmp-") }
        XCTAssert(tmpFiles.isEmpty, "Found orphaned tmp files: \(tmpFiles)")
    }

    /// Mode 0600 is not private on a folder that carries an inheritable allow ACE: the new
    /// file inherits the ACE and macOS evaluates ACEs before mode bits. The file and any
    /// directory this call creates must end with no ACL at all.
    func testInheritedACLEntriesAreStrippedFromTheFileAndCreatedDirectories() throws {
        let fm = FileManager.default
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        try grantEveryoneRead(at: dir.path, inheritable: true)
        // Control: a plain write DOES inherit, so the assertions below cannot pass vacuously.
        let control = dir.appendingPathComponent("control.json")
        try Data("{}".utf8).write(to: control)
        XCTAssertTrue(AtomicFile.hasACL(atPath: control.path), "the folder's ACE is inheritable")

        let url = dir.appendingPathComponent("nested/secret.json")
        try AtomicFile.write(Data("token".utf8), to: url)
        XCTAssertFalse(AtomicFile.hasACL(atPath: url.path))
        XCTAssertFalse(AtomicFile.hasACL(atPath: dir.appendingPathComponent("nested").path))
        XCTAssertEqual(try String(contentsOf: url, encoding: .utf8), "token")
    }

    /// The temp file is born in the app's own private folder and renamed into the target
    /// folder: a rename does not re-inherit ACEs, so there is no instant — not even a
    /// zero-byte one — at which a shared folder's principal can open it.
    func testStagingIsUsedOnTheSameVolumeAndSkippedAcrossVolumes() throws {
        let staging = dir.appendingPathComponent("staging")
        let target = dir.appendingPathComponent("shared")
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
        XCTAssertEqual(AtomicFile.stagingLocation(for: target, staging: staging), staging)
        XCTAssertEqual(try XCTUnwrap(FileManager.default.attributesOfItem(atPath: staging.path)[.posixPermissions] as? Int), 0o700, "created on demand, private")
        XCTAssertNil(AtomicFile.stagingLocation(for: URL(fileURLWithPath: "/dev"), staging: staging), "devfs is another device")
        XCTAssertNil(AtomicFile.stagingLocation(for: target, staging: nil))
    }

    func testWriteThroughStagingLeavesNothingBehind() throws {
        AtomicFile.privateStagingDirectory = dir.appendingPathComponent("staging")
        let shared = dir.appendingPathComponent("shared")
        try FileManager.default.createDirectory(at: shared, withIntermediateDirectories: true)
        try grantEveryoneRead(at: shared.path, inheritable: true)
        let url = shared.appendingPathComponent("secret.json")
        try AtomicFile.write(Data("one".utf8), to: url)
        try AtomicFile.write(Data("two".utf8), to: url)
        XCTAssertEqual(try String(contentsOf: url, encoding: .utf8), "two")
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: dir.appendingPathComponent("staging").path), [])
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: dir.appendingPathComponent("shared").path), ["secret.json"])
        XCTAssertFalse(AtomicFile.hasACL(atPath: url.path))
    }

    /// exFAT and other ACL-less volumes have nothing to strip: ENOTSUP from the ACL calls is
    /// success there, and a master list on a USB stick must keep saving.
    func testWritesSucceedOnAVolumeWithoutACLSupport() throws {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let image = dir.appendingPathComponent("exfat.dmg")
        let create = Process()
        create.executableURL = URL(fileURLWithPath: "/usr/bin/hdiutil")
        create.arguments = ["create", "-quiet", "-size", "8m", "-fs", "ExFAT", "-volname", "CCTEST", image.path]
        try create.run()
        create.waitUntilExit()
        try XCTSkipUnless(create.terminationStatus == 0, "hdiutil could not create an exFAT image here")

        let attach = Process()
        attach.executableURL = URL(fileURLWithPath: "/usr/bin/hdiutil")
        attach.arguments = ["attach", "-nobrowse", "-readwrite", "-plist", image.path]
        let output = Pipe()
        attach.standardOutput = output
        try attach.run()
        attach.waitUntilExit()
        try XCTSkipUnless(attach.terminationStatus == 0, "hdiutil could not attach the image here")
        let plist = try PropertyListSerialization.propertyList(from: output.fileHandleForReading.readDataToEndOfFile(), format: nil) as? [String: Any]
        let entities = plist?["system-entities"] as? [[String: Any]] ?? []
        let mountPoint = try XCTUnwrap(entities.compactMap { $0["mount-point"] as? String }.first)
        defer {
            let detach = Process()
            detach.executableURL = URL(fileURLWithPath: "/usr/bin/hdiutil")
            detach.arguments = ["detach", "-quiet", "-force", mountPoint]
            try? detach.run()
            detach.waitUntilExit()
        }

        let url = URL(fileURLWithPath: mountPoint).appendingPathComponent("store/mcps.json")
        try AtomicFile.write(Data("{}".utf8), to: url)
        try AtomicFile.write(Data("{\"v\":2}".utf8), to: url)
        XCTAssertEqual(try String(contentsOf: url, encoding: .utf8), "{\"v\":2}")
        XCTAssertFalse(AtomicFile.hasACL(atPath: url.path))
    }
}

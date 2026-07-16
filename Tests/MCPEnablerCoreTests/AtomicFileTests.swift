import XCTest
@testable import MCPEnablerCore

final class AtomicFileTests: XCTestCase {
    var dir: URL!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("atomic-\(UUID().uuidString)")
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
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
}

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
}

import XCTest
import ConnectorControlTestSupport
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/BoundedReadTests.cs
final class BoundedReadTests: XCTestCase {
    var tempDir: TempDir!
    var dir: URL!

    override func setUpWithError() throws {
        tempDir = TempDir(prefix: "bounded")
        dir = tempDir.url
    }

    override func tearDownWithError() throws {
        tempDir.dispose()
    }

    func testAFileUpToTheLimitIsReadWhole() throws {
        let url = dir.appendingPathComponent("doc.json")
        try Data("12345".utf8).write(to: url)
        XCTAssertEqual(try BoundedRead.data(at: url, limit: 5), Data("12345".utf8))
    }

    /// The read itself refuses what is over the limit, with no size checked beforehand to rely on. This
    /// file is refused by the open handle's size; the limit + 1 read covers one that grows after that
    /// check, which no test can make happen between the two deterministically.
    func testAFileOverTheLimitIsRefusedByTheReadItself() throws {
        let url = dir.appendingPathComponent("doc.json")
        try Data("123456".utf8).write(to: url)
        XCTAssertThrowsError(try BoundedRead.data(at: url, limit: 5)) {
            XCTAssertEqual($0 as? BoundedReadError, .tooLarge)
            XCTAssertEqual($0.localizedDescription, BoundedRead.tooLargeError)
        }
    }

    func testAFolderIsNotAFile() throws {
        XCTAssertThrowsError(try BoundedRead.data(at: dir, limit: 5)) {
            XCTAssertEqual($0 as? BoundedReadError, .notAFile)
            XCTAssertEqual($0.localizedDescription, BoundedRead.notAFileError)
        }
    }

    /// A FIFO would hold an ordinary read until something wrote to it; this one is refused at once.
    /// The Windows mirror has no such file to make.
    func testAFIFOIsRefusedWithoutWaiting() throws {
        let url = dir.appendingPathComponent("pipe.json")
        XCTAssertEqual(mkfifo(url.path, 0o600), 0)
        XCTAssertThrowsError(try BoundedRead.data(at: url, limit: 5)) {
            XCTAssertEqual($0 as? BoundedReadError, .notAFile)
        }
    }

    /// A file that is not there is reported in the system's own words, as an unbounded read did.
    func testAMissingFileSaysWhatAnUnboundedReadSaid() throws {
        let url = dir.appendingPathComponent("gone.json")
        let unbounded = try XCTUnwrap({ () -> Error? in
            do { _ = try Data(contentsOf: url); return nil } catch { return error }
        }())
        XCTAssertThrowsError(try BoundedRead.data(at: url, limit: 5)) {
            XCTAssertEqual($0.localizedDescription, unbounded.localizedDescription)
        }
    }
}

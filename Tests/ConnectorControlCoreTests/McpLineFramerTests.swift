import XCTest
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/McpLineFramerTests.cs
final class McpLineFramerTests: XCTestCase {
    private func bytes(_ text: String) -> Data { Data(text.utf8) }

    func testLinesAreSplitAtNewlinesWhereverTheChunksBreak() {
        var framer = McpLineFramer(limit: 10)
        XCTAssertEqual(framer.append(bytes("ab")), [])
        XCTAssertEqual(framer.append(bytes("c\nde\n\nf")), [.line(bytes("abc")), .line(bytes("de")), .line(Data())])
        XCTAssertEqual(framer.finish(), [.line(bytes("f"))], "a last line without its newline is still a line")
        XCTAssertEqual(framer.finish(), [])
    }

    func testALineAtTheLimitPassesAndOnePastItIsReportedOnceAndSkipped() {
        var framer = McpLineFramer(limit: 4)
        XCTAssertEqual(framer.append(bytes("abcd\n")), [.line(bytes("abcd"))])
        XCTAssertEqual(framer.append(bytes("abc")), [])
        XCTAssertEqual(framer.append(bytes("de")), [.tooLarge], "reported as soon as it passes the limit")
        XCTAssertEqual(framer.append(bytes("fgh")), [], "the rest of that line is dropped as it comes")
        XCTAssertEqual(framer.append(bytes("ij\nok\n")), [.line(bytes("ok"))], "the stream carries on after it")
        XCTAssertEqual(framer.append(bytes("toolong\n")), [.tooLarge], "a whole line past the limit in one chunk")
        XCTAssertEqual(framer.append(bytes("waytoolong")), [.tooLarge])
        XCTAssertEqual(framer.finish(), [], "a skipped line is not a line at the end either")
    }

    func testACarriageReturnStaysInTheLineAndACharacterMayBeSplitBetweenChunks() {
        var framer = McpLineFramer(limit: 10)
        XCTAssertEqual(framer.append(bytes("ab\r\n")), [.line(bytes("ab\r"))], "only the newline is stripped; the session trims the rest")
        XCTAssertEqual(framer.append(Data([0xC3])), [])
        XCTAssertEqual(framer.append(Data([0xA9, 0x0A])), [.line(bytes("é"))], "bytes, not characters: a split character joins up")
    }

    func testTheDefaultLimitIsOneMegabyte() {
        var framer = McpLineFramer()
        XCTAssertEqual(framer.limit, McpSession.maxMessageBytes)
        let exact = Data(repeating: 0x20, count: McpSession.maxMessageBytes)
        XCTAssertEqual(framer.append(exact + bytes("\n")), [.line(exact)])
        XCTAssertEqual(framer.append(exact + bytes(" \n")), [.tooLarge])
    }
}

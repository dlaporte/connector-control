import XCTest
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ReadmeHelpTests.cs
///
/// README.md's command reference is `connector-control help`'s own output, pasted between two
/// markers. A command added, renamed or re-described without the README following fails here.
@MainActor
final class ReadmeHelpTests: XCTestCase {
    static let begin = "<!-- connector-control help: begin -->"
    static let end = "<!-- connector-control help: end -->"

    /// Platform idiom: found from this file's own path; the Windows mirror reads the copy its
    /// project file places beside the test binary.
    static var readmeURL: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()   // Tests/ConnectorControlStateTests
            .deletingLastPathComponent()   // Tests
            .deletingLastPathComponent()   // the repository
            .appendingPathComponent("README.md")
    }

    /// The lines inside the one ```text fence between the markers, or nil when the README has no
    /// such block or more than one. Line endings are read either way: a Windows checkout may
    /// convert them.
    static func helpBlock(_ readme: String) -> String? {
        let lines = readme.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
        guard lines.filter({ $0 == begin }).count == 1, lines.filter({ $0 == end }).count == 1,
              let first = lines.firstIndex(of: begin), let last = lines.firstIndex(of: end), first < last
        else { return nil }
        let inner = Array(lines[(first + 1)..<last])
        guard inner.count >= 2, inner.first == "```text", inner.last == "```" else { return nil }
        return inner.dropFirst().dropLast().joined(separator: "\n")
    }

    func testTheReadmeListsExactlyWhatHelpPrints() throws {
        let readme = try String(contentsOf: Self.readmeURL, encoding: .utf8)
        let block = try XCTUnwrap(Self.helpBlock(readme),
                                  "README.md needs one ```text block between \(Self.begin) and \(Self.end)")
        XCTAssertEqual(block, ControlTools.helpText(),
                       "README.md's command reference differs from `connector-control help`: paste the tool's output between the markers")
    }

    func testTheBlockIsReadOnlyFromBetweenOneMarkerPair() {
        let (b, e) = (Self.begin, Self.end)
        XCTAssertNil(Self.helpBlock("no markers"))
        XCTAssertEqual(Self.helpBlock("x\n\(b)\n```text\na\n  b\n```\n\(e)\ny"), "a\n  b")
        XCTAssertEqual(Self.helpBlock("\(b)\r\n```text\r\na\r\n```\r\n\(e)\r\n"), "a")
        XCTAssertNil(Self.helpBlock("\(b)\na\n\(e)"))                           // no fence
        XCTAssertNil(Self.helpBlock("\(e)\n```text\na\n```\n\(b)"))              // markers reversed
        XCTAssertNil(Self.helpBlock("\(b)\n```text\na\n```\n\(e)\n\(b)\n```text\na\n```\n\(e)"))   // two blocks
    }
}

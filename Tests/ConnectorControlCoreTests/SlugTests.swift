import XCTest
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/SlugTests.cs
final class SlugTests: XCTestCase {
    func testLowercasesAndHyphenatesRuns() {
        XCTAssertEqual(Slug.make("Data team"), "data-team")
        XCTAssertEqual(Slug.make("  Acme -- Consulting (2026) "), "acme-consulting-2026")
    }
    func testDropsNonASCIIAndNeverStartsOrEndsWithAHyphen() {
        XCTAssertEqual(Slug.make("Équipe données"), "quipe-donn-es")
        XCTAssertEqual(Slug.make("---"), "collection")
        XCTAssertEqual(Slug.make(""), "collection")
    }
    /// Swift-only half of a known difference (see `Slug.make`): the C# test pins the other half.
    func testALetterWhoseLowercaseExpandsSlugsAsTheMacLowercasesIt() {
        XCTAssertEqual(Slug.make("\u{130}stanbul"), "i-stanbul")
    }
}

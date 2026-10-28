import XCTest
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ControlVersionTests.cs
final class ControlVersionTests: XCTestCase {
    func testTheSemanticPartOfAVersionAsTheAppsShowIt() {
        XCTAssertEqual(ControlVersion.semantic("1.5.0"), "1.5.0")
        XCTAssertEqual(ControlVersion.semantic("1.5.0 (10500)"), "1.5.0", "the Mac's build number goes")
        XCTAssertEqual(ControlVersion.semantic("1.5.0-preview.3 (10499.3)"), "1.5.0-preview.3")
        XCTAssertEqual(ControlVersion.semantic("1.5.0+1a2b3c4def"), "1.5.0", ".NET's source revision goes")
        XCTAssertEqual(ControlVersion.semantic("1.5.0-preview.3+1a2b3c4"), "1.5.0-preview.3")
        XCTAssertEqual(ControlVersion.semantic("1.5.0-"), "1.5.0", "a dash with nothing after it is no prerelease")
        XCTAssertNil(ControlVersion.semantic("development build"))
        XCTAssertNil(ControlVersion.semantic("1.5"))
        XCTAssertNil(ControlVersion.semantic(""))
    }

    func testPrecedenceFollowsSemanticVersioning() {
        XCTAssertTrue(ControlVersion.isNewer("1.6.0", than: "1.5.0"))
        XCTAssertTrue(ControlVersion.isNewer("1.5.10", than: "1.5.9"), "numbers, not text")
        XCTAssertTrue(ControlVersion.isNewer("2.0.0", than: "1.99.99"))
        XCTAssertTrue(ControlVersion.isNewer("1.5.0", than: "1.5.0-preview.3"), "a release comes after its previews")
        XCTAssertTrue(ControlVersion.isNewer("1.5.0-preview.10", than: "1.5.0-preview.9"))
        XCTAssertTrue(ControlVersion.isNewer("1.5.0-preview.a", than: "1.5.0-preview.9"), "letters after numbers")
        XCTAssertTrue(ControlVersion.isNewer("1.5.0-preview.3.1", than: "1.5.0-preview.3"), "more identifiers, the rest equal")
        XCTAssertTrue(ControlVersion.isNewer("1.5.0 (10500)", than: "1.4.0"), "display forms compare by their version")
        XCTAssertFalse(ControlVersion.isNewer("1.5.0", than: "1.5.0"))
        XCTAssertFalse(ControlVersion.isNewer("1.5.0-preview.3", than: "1.5.0"))
        XCTAssertFalse(ControlVersion.isNewer("development build", than: "1.5.0"))
        XCTAssertFalse(ControlVersion.isNewer("1.6.0", than: "development build"))
        XCTAssertFalse(ControlVersion.isNewer("99999999999999999999.0.0", than: "1.0.0"), "a number past 64 bits is no version")
        XCTAssertTrue(ControlVersion.isNewer("1.5.0-a.-0", than: "1.5.0-a.1"), "\"-0\" is a word, and words come after numbers")
        XCTAssertFalse(ControlVersion.isNewer("1.5.0-a.01", than: "1.5.0-a.1"), "the same number")
        XCTAssertTrue(ControlVersion.isNewer("1.5.0-a.18446744073709551616", than: "1.5.0-a.18446744073709551615"),
                      "a prerelease number past 64 bits still compares by value")
    }
}

import XCTest
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/ContentHashTests.cs
final class ContentHashTests: XCTestCase {
    func testKnownVector() {
        XCTAssertEqual(ContentHash.sha256(Data("abc".utf8)),
                       "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
    }
}

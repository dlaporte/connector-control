import XCTest
@testable import ConnectorControlState

final class AppVersionTests: XCTestCase {
    func testDisplayFollowsTheBundleKeys() {
        XCTAssertEqual(AppVersion.display(info: nil), "development build")
        XCTAssertEqual(AppVersion.display(info: ["CFBundleVersion": "10300"]), "development build",
                       "no marketing version means no bundle worth naming")
        XCTAssertEqual(AppVersion.display(info: ["CFBundleShortVersionString": "1.3.0"]), "1.3.0")
        XCTAssertEqual(AppVersion.display(info: ["CFBundleShortVersionString": "1.3.0", "CFBundleVersion": "1.3.0"]), "1.3.0")
        XCTAssertEqual(AppVersion.display(info: ["CFBundleShortVersionString": "1.3.0", "CFBundleVersion": "10300"]), "1.3.0 (10300)")
    }
}

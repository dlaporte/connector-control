import XCTest
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/PathContextTests.cs
final class PathContextTests: XCTestCase {
    func testLiveReadsTheProcessEnvironmentAndApplicationSupport() {
        setenv("CONNECTOR_CONTROL_PLAN_PROBE", "yes", 1)
        defer { unsetenv("CONNECTOR_CONTROL_PLAN_PROBE") }
        let live = PathContext.live()
        XCTAssertEqual(live.environment["CONNECTOR_CONTROL_PLAN_PROBE"], "yes")
        XCTAssertEqual(live.appSupport,
                       FileManager.default.homeDirectoryForCurrentUser
                           .appendingPathComponent("Library/Application Support"))
    }
}

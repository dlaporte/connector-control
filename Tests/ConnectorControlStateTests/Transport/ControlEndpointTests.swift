import XCTest
import ConnectorControlCore
@testable import ConnectorControlTransport

/// Mac only, apart from `mayStartApp`: the Windows counterpart is
/// windows/tests/ConnectorControl.Core.Tests/Transport/ControlEndpointTests.cs.
final class ControlEndpointTests: XCTestCase {
    private let support = URL(fileURLWithPath: "/Users/me/Library/Application Support")

    func testTheSocketIsInTheMachineLocalDataFolderUnlessADevelopmentRunMovesIt() {
        let local = "/Users/me/Library/Application Support/Connector Control/control.sock"
        XCTAssertEqual(ControlEndpoint.socketPath(environment: [:], appSupport: support), local)
        XCTAssertEqual(ControlEndpoint.socketPath(environment: [AppPaths.storeDirEnv: "/tmp/cc-x/store"], appSupport: support),
                       "/tmp/cc-x/store/control.sock")
        XCTAssertEqual(ControlEndpoint.socketPath(environment: [AppPaths.storeDirEnv: ""], appSupport: support), local,
                       "an empty override is no override")
    }

    func testTheToolStartsTheAppOnlyAgainstTheLiveStore() {
        XCTAssertTrue(ControlEndpoint.mayStartApp(environment: [:]))
        XCTAssertTrue(ControlEndpoint.mayStartApp(environment: [AppPaths.storeDirEnv: ""]))
        XCTAssertFalse(ControlEndpoint.mayStartApp(environment: [AppPaths.storeDirEnv: "/tmp/cc-x/store"]))
    }

    func testTheBundledToolSitsBesideTheAppsExecutable() {
        XCTAssertEqual(ControlEndpoint.bundledToolPath(
            appExecutable: URL(fileURLWithPath: "/Applications/Connector Control.app/Contents/MacOS/Connector Control")),
                       "/Applications/Connector Control.app/Contents/MacOS/connector-control")
        XCTAssertEqual(ControlEndpoint.bundledToolPath(appExecutable: URL(fileURLWithPath: "/src/cc/.build/debug/ConnectorControl")),
                       "/src/cc/.build/debug/connector-control", "a development build's tool is built beside it")
    }
}

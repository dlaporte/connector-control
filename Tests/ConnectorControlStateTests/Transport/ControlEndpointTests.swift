import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState
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

    /// The app it would start edits the live Claude config, not the one named.
    func testTheToolStartsNoAppWhileAnotherClaudeConfigIsNamed() {
        XCTAssertTrue(ControlEndpoint.mayStartApp(environment: [AppPaths.claudeConfigEnv: ""]))
        XCTAssertFalse(ControlEndpoint.mayStartApp(environment: [AppPaths.claudeConfigEnv: "/tmp/cc-x/claude_desktop_config.json"]))
    }

    func testOnlyRootIsElevated() {
        XCTAssertTrue(ControlEndpoint.isElevated(effectiveUserID: 0), "under sudo")
        XCTAssertFalse(ControlEndpoint.isElevated(effectiveUserID: 501))
    }

    func testTheBundledToolSitsBesideTheAppsExecutable() {
        XCTAssertEqual(ControlEndpoint.bundledToolPath(
            appExecutable: URL(fileURLWithPath: "/Applications/Connector Control.app/Contents/MacOS/Connector Control")),
                       "/Applications/Connector Control.app/Contents/MacOS/connector-control")
        XCTAssertEqual(ControlEndpoint.bundledToolPath(appExecutable: URL(fileURLWithPath: "/src/cc/.build/debug/ConnectorControl")),
                       "/src/cc/.build/debug/connector-control", "a development build's tool is built beside it")
    }

    func testTheToolKnowsTheBundleItIsIn() {
        XCTAssertEqual(ControlEndpoint.enclosingBundle(ofTool: "/Applications/Connector Control.app/Contents/MacOS/connector-control"),
                       "/Applications/Connector Control.app")
        XCTAssertNil(ControlEndpoint.enclosingBundle(ofTool: "/src/cc/.build/debug/connector-control"))
        XCTAssertNil(ControlEndpoint.enclosingBundle(ofTool: "/usr/local/bin/connector-control"), "a link is resolved before this is asked")
    }

    func testAnAppRunFromWhereItWillNotStayIsTransient() {
        let translocated = URL(fileURLWithPath: "/private/var/folders/xy/T/AppTranslocation/0A1B/d/Connector Control.app")
        XCTAssertTrue(ControlEndpoint.isTransient(bundle: translocated, volumeIsReadOnly: { _ in false }))
        XCTAssertTrue(ControlEndpoint.isTransient(bundle: URL(fileURLWithPath: "/Volumes/Connector Control/Connector Control.app"),
                                                  volumeIsReadOnly: { _ in true }), "its disk image")
        XCTAssertFalse(ControlEndpoint.isTransient(bundle: URL(fileURLWithPath: "/Applications/Connector Control.app"),
                                                   volumeIsReadOnly: { _ in false }))
        let dir = TempDir(prefix: "transient"); defer { dir.dispose() }
        XCTAssertFalse(ControlEndpoint.isTransient(bundle: dir.url), "a folder on this Mac's own disk, read for real")
    }

    func testTheToolOpensOnlyTheAppItCameWithAndWithoutBringingItForward() {
        var asked: [[String]] = []
        XCTAssertTrue(ControlEndpoint.launchApp(executablePath: "/Applications/Connector Control.app/Contents/MacOS/connector-control",
                                                run: { asked.append($0); return 0 }))
        XCTAssertFalse(ControlEndpoint.launchApp(executablePath: "/src/cc/.build/debug/connector-control",
                                                 run: { asked.append($0); return 0 }), "a tool outside a bundle starts nothing")
        XCTAssertEqual(asked, [["-g", "/Applications/Connector Control.app"]])
    }

    func testTheToolsVersionIsItsAppsVersion() throws {
        let dir = TempDir(prefix: "toolversion")
        defer { dir.dispose() }
        let app = dir.file("Connector Control.app")
        try FileManager.default.createDirectory(at: app.appendingPathComponent("Contents/MacOS"), withIntermediateDirectories: true)
        let info: NSDictionary = ["CFBundleShortVersionString": "1.5.0", "CFBundleVersion": "10500"]
        XCTAssertTrue(info.write(to: app.appendingPathComponent("Contents/Info.plist"), atomically: true))
        XCTAssertEqual(ControlEndpoint.toolVersion(executablePath: app.appendingPathComponent("Contents/MacOS/connector-control").path),
                       "1.5.0", "the build number the Mac shows beside it is not part of the version")
        XCTAssertEqual(ControlEndpoint.toolVersion(executablePath: "/src/cc/.build/debug/connector-control"), AppVersion.developmentBuild)
    }

    func testTheToolSaysItsVersionOnlyWhenItIsOne() {
        XCTAssertEqual(ControlEndpoint.toolVersion(stamped: "1.5.0"), "1.5.0")
        XCTAssertEqual(ControlEndpoint.toolVersion(stamped: "1.5.0-preview.1"), "1.5.0-preview.1")
        XCTAssertEqual(ControlEndpoint.toolVersion(stamped: "0.0.0"), AppVersion.developmentBuild, "what nobody stamped")
        XCTAssertEqual(ControlEndpoint.toolVersion(stamped: ""), AppVersion.developmentBuild)
        XCTAssertEqual(ControlEndpoint.toolVersion(stamped: "1.0"), AppVersion.developmentBuild,
                       "build-app.sh's default is not a version the hello can carry")
    }

    func testABundleStampedWithNoVersionIsADevelopmentBuild() throws {
        let dir = TempDir(prefix: "toolversion-unstamped")
        defer { dir.dispose() }
        let app = dir.file("Connector Control.app")
        try FileManager.default.createDirectory(at: app.appendingPathComponent("Contents/MacOS"), withIntermediateDirectories: true)
        let info: NSDictionary = ["CFBundleShortVersionString": "1.0", "CFBundleVersion": "1"]
        XCTAssertTrue(info.write(to: app.appendingPathComponent("Contents/Info.plist"), atomically: true))
        XCTAssertEqual(ControlEndpoint.toolVersion(executablePath: app.appendingPathComponent("Contents/MacOS/connector-control").path),
                       AppVersion.developmentBuild)
    }
}

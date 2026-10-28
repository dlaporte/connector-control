// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "ConnectorControl",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "ConnectorControl", targets: ["ConnectorControl"]),
        // The command-line tool build-app.sh puts at Contents/MacOS/connector-control.
        .executable(name: "connector-control", targets: ["ConnectorControlCLI"]),
    ],
    dependencies: [
        .package(url: "https://github.com/sparkle-project/Sparkle", from: "2.9.0"),
    ],
    targets: [
        .target(name: "ConnectorControlCore"),
        // The app's state layer: AppState and the surface models behind protocol
        // seams. Foundation + Combine only, so the whole state machine runs under
        // `swift test` with fakes, like the Windows port's ConnectorControl.Core/State.
        .target(
            name: "ConnectorControlState",
            dependencies: ["ConnectorControlCore"]),
        // Everything between bytes and ControlTools: the server the apps run, the client and relay
        // the command-line tool runs, and the Mac's socket. Foundation only, so it runs under
        // `swift test`; the Windows port keeps the same code in ConnectorControl.Core/Transport.
        .target(
            name: "ConnectorControlTransport",
            dependencies: ["ConnectorControlCore", "ConnectorControlState"]),
        .executableTarget(
            name: "ConnectorControlCLI",
            dependencies: ["ConnectorControlCore", "ConnectorControlState", "ConnectorControlTransport"]),
        .executableTarget(
            name: "ConnectorControl",
            dependencies: [
                "ConnectorControlCore",
                "ConnectorControlState",
                "ConnectorControlTransport",
                .product(name: "Sparkle", package: "Sparkle"),
            ]),
        // Test support shared by both test targets (TempDir, fixtures, ACL
        // helpers, …). A regular target, not a test target, so it can be a
        // dependency of both; it imports XCTest itself for the helpers that
        // need to fail a test (e.g. grantEveryoneRead). Not a dependency of
        // the executable, so XCTest never links into the shipped app.
        .target(
            name: "ConnectorControlTestSupport",
            dependencies: ["ConnectorControlCore"],
            path: "Tests/ConnectorControlTestSupport"),
        .testTarget(
            name: "ConnectorControlCoreTests",
            dependencies: ["ConnectorControlCore", "ConnectorControlTestSupport"]),
        .testTarget(
            name: "ConnectorControlStateTests",
            dependencies: ["ConnectorControlState", "ConnectorControlTransport", "ConnectorControlTestSupport"]),
    ]
)

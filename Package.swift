// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "ConnectorControl",
    platforms: [.macOS(.v14)],
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
        .executableTarget(
            name: "ConnectorControl",
            dependencies: [
                "ConnectorControlCore",
                "ConnectorControlState",
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
            dependencies: ["ConnectorControlState", "ConnectorControlTestSupport"]),
    ]
)

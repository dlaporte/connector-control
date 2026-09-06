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
        .testTarget(name: "ConnectorControlCoreTests", dependencies: ["ConnectorControlCore"]),
        .testTarget(name: "ConnectorControlStateTests", dependencies: ["ConnectorControlState"]),
    ]
)

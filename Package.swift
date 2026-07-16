// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "ConnectorControl",
    platforms: [.macOS(.v14)],
    targets: [
        .target(name: "ConnectorControlCore"),
        .executableTarget(name: "ConnectorControl", dependencies: ["ConnectorControlCore"]),
        .testTarget(name: "ConnectorControlCoreTests", dependencies: ["ConnectorControlCore"]),
    ]
)

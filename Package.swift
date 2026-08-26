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
        .executableTarget(
            name: "ConnectorControl",
            dependencies: [
                "ConnectorControlCore",
                .product(name: "Sparkle", package: "Sparkle"),
            ]),
        .testTarget(name: "ConnectorControlCoreTests", dependencies: ["ConnectorControlCore"]),
    ]
)

// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "MCPEnabler",
    platforms: [.macOS(.v14)],
    targets: [
        .target(name: "MCPEnablerCore"),
        .executableTarget(name: "MCPEnabler", dependencies: ["MCPEnablerCore"]),
        .testTarget(name: "MCPEnablerCoreTests", dependencies: ["MCPEnablerCore"]),
    ]
)

import Foundation

/// Everything path resolution needs from the machine (catalog §1.3), injectable
/// for tests: the process environment (the two CONNECTOR_CONTROL_* overrides)
/// and the Application Support directory the defaults hang off.
public struct PathContext: Sendable {
    public let environment: [String: String]
    public let appSupport: URL

    public init(environment: [String: String], appSupport: URL) {
        self.environment = environment
        self.appSupport = appSupport
    }

    public static func live() -> PathContext {
        PathContext(
            environment: ProcessInfo.processInfo.environment,
            appSupport: FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent("Library/Application Support"))
    }
}

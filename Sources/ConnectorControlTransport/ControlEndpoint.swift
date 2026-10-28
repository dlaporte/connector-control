import Foundation
import ConnectorControlCore

/// Mac only: the Windows counterpart is windows/src/ConnectorControl.Core/Transport/ControlEndpoint.cs.
///
/// Where the app's endpoint and its tool are, worked out the same way by the app and by the tool.
public enum ControlEndpoint {
    public static let socketName = "control.sock"
    public static let toolName = "connector-control"

    /// The socket: in the machine-local data folder — the default store folder, never the synced
    /// master-list folder a person may choose — or in the store folder CONNECTOR_CONTROL_STORE_DIR
    /// names for a development run or a test.
    public static func socketPath(environment: [String: String], appSupport: URL) -> String {
        AppPaths.live(environment: environment, appSupport: appSupport).storeDirURL
            .appendingPathComponent(socketName).path
    }

    /// Whether the tool may start the app when nothing answers: never while CONNECTOR_CONTROL_STORE_DIR
    /// is set, since the app it would start reads the live store, not the one named.
    public static func mayStartApp(environment: [String: String]) -> Bool {
        (environment[AppPaths.storeDirEnv] ?? "").isEmpty
    }

    /// The tool beside the app's own executable: `Contents/MacOS/connector-control` in the bundle, and
    /// beside the binary in a development build's `.build` folder.
    public static func bundledToolPath(appExecutable: URL) -> String {
        appExecutable.deletingLastPathComponent().appendingPathComponent(toolName).path
    }
}

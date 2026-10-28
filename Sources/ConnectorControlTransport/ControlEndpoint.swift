import Foundation
import MachO
import ConnectorControlCore
import ConnectorControlState

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

    /// Whether the app runs from somewhere it will not stay: a translocated copy — what macOS runs when
    /// a quarantined app is opened where it was downloaded — or a read-only volume, such as its disk
    /// image. A path there is gone once the app is moved or the image ejected.
    public static func isTransient(bundle: URL, volumeIsReadOnly: (URL) -> Bool = ControlEndpoint.volumeIsReadOnly) -> Bool {
        bundle.path.contains("/AppTranslocation/") || volumeIsReadOnly(bundle)
    }

    /// Whether `url` is on a volume nothing can be written to.
    public static func volumeIsReadOnly(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.volumeIsReadOnlyKey]))?.volumeIsReadOnly ?? false
    }

    /// This process's executable, links resolved: the tool inside the app, also when it was run
    /// through the link in /usr/local/bin.
    public static func executablePath() -> String {
        var size: UInt32 = 0
        _ = _NSGetExecutablePath(nil, &size)
        var buffer = [CChar](repeating: 0, count: Int(size) + 1)
        let raw = _NSGetExecutablePath(&buffer, &size) == 0 ? String(cString: buffer) : CommandLine.arguments[0]
        return URL(fileURLWithPath: raw).resolvingSymlinksInPath().path
    }

    /// The app bundle the tool runs from — `…/Connector Control.app` for its
    /// `…/Contents/MacOS/connector-control` — or nil for a tool outside one, as a development build's.
    public static func enclosingBundle(ofTool path: String) -> String? {
        let macOS = URL(fileURLWithPath: path).deletingLastPathComponent()
        let contents = macOS.deletingLastPathComponent()
        let bundle = contents.deletingLastPathComponent()
        guard macOS.lastPathComponent == "MacOS", contents.lastPathComponent == "Contents", bundle.pathExtension == "app" else {
            return nil
        }
        return bundle.path
    }

    /// The version the tool says in its hello and prints for `--version`: its app's marketing
    /// version, read from the bundle it is in — never the build number the Mac's Settings shows
    /// beside it.
    public static func toolVersion(executablePath: String) -> String {
        guard let bundle = enclosingBundle(ofTool: executablePath),
              let info = NSDictionary(contentsOfFile: bundle + "/Contents/Info.plist") as? [String: Any],
              let short = info["CFBundleShortVersionString"] as? String else {
            return AppVersion.developmentBuild
        }
        return toolVersion(stamped: short)
    }

    /// What the tool says for the version a build stamped: its SemVer, or "development build" for a stamp
    /// that is none — build-app.sh's default `1.0`, an empty one — or for `0.0.0`, what a build nobody
    /// stamped carries on Windows, so both platforms call such a build the same thing.
    public static func toolVersion(stamped: String) -> String {
        guard let version = ControlVersion.semantic(stamped), version != "0.0.0" else { return AppVersion.developmentBuild }
        return version
    }

    /// Opens the app the tool came with, without bringing it forward: the bundle the tool runs from,
    /// which the /usr/local/bin link resolves into. A tool outside a bundle — a development build —
    /// starts nothing, as a Windows tool that is not the packaged one starts nothing. False when nothing
    /// opened.
    public static func launchApp(executablePath: String, run: ([String]) -> Int32 = runOpen) -> Bool {
        guard let bundle = enclosingBundle(ofTool: executablePath) else { return false }
        return run(["-g", bundle]) == 0
    }

    /// `/usr/bin/open` with `arguments`; its exit status. Public, as the default of a public function's
    /// argument must be. Its standard streams are the null device: the relay's are Claude's MCP stream,
    /// which `open` must neither read nor write. Platform-forced: LaunchServices starts the app with
    /// launchd's own environment, not the tool's, where Windows' detached start inherits the tool's.
    public static func runOpen(_ arguments: [String]) -> Int32 {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        process.arguments = arguments
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
        } catch {
            return -1
        }
        process.waitUntilExit()
        return process.terminationStatus
    }
}

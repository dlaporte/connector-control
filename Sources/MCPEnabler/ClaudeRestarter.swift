import AppKit

enum ClaudeRestarter {
    static let bundleID = "com.anthropic.claudefordesktop"

    /// Gracefully terminate Claude (never force-kill), wait up to 15 s, relaunch.
    /// Calls completion on the main queue with nil on success or an error message.
    static func restart(
        appURL: URL = URL(fileURLWithPath: "/Applications/Claude.app"),
        completion: @escaping (String?) -> Void) {
        guard FileManager.default.fileExists(atPath: appURL.path) else {
            DispatchQueue.main.async {
                completion("Claude.app was not found at \(appURL.path).")
            }
            return
        }
        let running = NSRunningApplication.runningApplications(
            withBundleIdentifier: bundleID)
        running.forEach { $0.terminate() }

        DispatchQueue.global().async {
            let deadline = Date().addingTimeInterval(15)
            while Date() < deadline {
                let still = NSRunningApplication.runningApplications(
                    withBundleIdentifier: bundleID)
                if still.allSatisfy(\.isTerminated) || still.isEmpty { break }
                Thread.sleep(forTimeInterval: 0.25)
            }
            let stillRunning = !NSRunningApplication.runningApplications(
                withBundleIdentifier: bundleID).isEmpty
            DispatchQueue.main.async {
                if stillRunning {
                    completion("Claude didn’t quit (it may be showing a dialog). "
                               + "Quit it manually, then click Restart Claude again.")
                    return
                }
                NSWorkspace.shared.openApplication(
                    at: appURL,
                    configuration: NSWorkspace.OpenConfiguration()
                ) { _, error in
                    DispatchQueue.main.async {
                        completion(error?.localizedDescription)
                    }
                }
            }
        }
    }
}

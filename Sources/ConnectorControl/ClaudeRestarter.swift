import AppKit
import Security
import ConnectorControlState

enum ClaudeRestarter {
    static let bundleID = "com.anthropic.claudefordesktop"
    /// Anthropic PBC's Developer ID team, as on every shipped Claude.app.
    static let teamIdentifier = "Q6L2SF6YDW"
    /// Claude Desktop, signed by Anthropic — under either a Developer ID or an
    /// App Store certificate chained to Apple. The path this app launches is a
    /// plain string in UserDefaults, so before it quits Claude and starts
    /// whatever sits at that path, the bundle has to prove it is Claude.
    static let requirement =
        "anchor apple generic and identifier \"\(bundleID)\" and certificate leaf[subject.OU] = \"\(teamIdentifier)\""
    static let quitTimeout: TimeInterval = 15
    static let pollInterval: TimeInterval = 0.25

    /// Gracefully terminate Claude (never force-kill), wait up to quitTimeout,
    /// relaunch. Calls completion with nil on success or an error message; the
    /// ClaudeProcess protocol says any thread is fine — AppState marshals it.
    static func restart(appURL: URL, completion: @escaping (String?) -> Void) {
        guard FileManager.default.fileExists(atPath: appURL.path) else {
            completion("Claude Desktop was not found at \(appURL.path).")
            return
        }
        // Verified BEFORE anything is quit: a bundle that fails the check must
        // not cost the user the Claude they have running. The check reads the
        // whole bundle, so it runs off the main thread.
        DispatchQueue.global().async {
            if let problem = verifyIsClaude(at: appURL) {
                completion(problem)
                return
            }
            terminateAndRelaunch(appURL: appURL, completion: completion)
        }
    }

    /// nil when the bundle at `appURL` is Claude Desktop signed by Anthropic;
    /// otherwise the user-facing reason it must not be launched. Slow (it
    /// validates the whole bundle): call it off the main thread.
    static func verifyIsClaude(at appURL: URL) -> String? {
        var staticCode: SecStaticCode?
        guard SecStaticCodeCreateWithPath(appURL as CFURL, [], &staticCode) == errSecSuccess,
              let code = staticCode else {
            return "\(appURL.lastPathComponent) is not an app bundle this app can inspect."
        }
        var compiled: SecRequirement?
        guard SecRequirementCreateWithString(requirement as CFString, [], &compiled) == errSecSuccess,
              let requirement = compiled else {
            return "The Claude Desktop signing requirement could not be compiled."
        }
        var error: Unmanaged<CFError>?
        let status = SecStaticCodeCheckValidityWithErrors(code, [], requirement, &error)
        if status == errSecSuccess { return nil }
        let detail: String
        if let error = error?.takeRetainedValue() {
            detail = CFErrorCopyDescription(error) as String
        } else if let message = SecCopyErrorMessageString(status, nil) {
            detail = message as String
        } else {
            detail = "code \(status)"
        }
        return "\(appURL.lastPathComponent) is not Claude Desktop signed by Anthropic (\(detail)). "
            + AppState.chooseClaude
    }

    /// Runs on the background queue `restart` dispatched to; only the actual
    /// `terminate()` calls are pushed onto main, as AppKit expects.
    private static func terminateAndRelaunch(appURL: URL, completion: @escaping (String?) -> Void) {
        DispatchQueue.main.sync {
            NSRunningApplication.runningApplications(withBundleIdentifier: bundleID)
                .forEach { $0.terminate() }
        }

        let deadline = Date().addingTimeInterval(quitTimeout)
        while Date() < deadline {
            let still = NSRunningApplication.runningApplications(
                withBundleIdentifier: bundleID)
            if still.allSatisfy(\.isTerminated) || still.isEmpty { break }
            Thread.sleep(forTimeInterval: pollInterval)
        }
        let stillRunning = !NSRunningApplication.runningApplications(
            withBundleIdentifier: bundleID).isEmpty
        if stillRunning {
            completion("Claude didn’t quit (it may be showing a dialog). "
                       + "Quit it manually, then click Restart Claude again.")
            return
        }
        NSWorkspace.shared.openApplication(
            at: appURL,
            configuration: NSWorkspace.OpenConfiguration()
        ) { _, error in
            completion(error?.localizedDescription)
        }
    }
}

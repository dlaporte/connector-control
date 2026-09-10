import AppKit
import Security

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
        // Verified BEFORE anything is quit: a bundle that fails the check must
        // not cost the user the Claude they have running. The check reads the
        // whole bundle, so it runs off the main thread.
        DispatchQueue.global().async {
            if let problem = verifyIsClaude(at: appURL) {
                DispatchQueue.main.async { completion(problem) }
                return
            }
            DispatchQueue.main.async { terminateAndRelaunch(appURL: appURL, completion: completion) }
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
            + "Choose the real Claude.app under Settings ▸ Claude."
    }

    private static func terminateAndRelaunch(appURL: URL, completion: @escaping (String?) -> Void) {
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

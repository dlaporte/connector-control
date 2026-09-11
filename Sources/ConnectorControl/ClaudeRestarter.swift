import AppKit
import Security
import ConnectorControlState

enum ClaudeRestarter {
    static let quitTimeout: TimeInterval = 15
    static let pollInterval: TimeInterval = 0.25

    /// Gracefully terminate Claude (never force-kill), wait up to quitTimeout,
    /// relaunch. Calls completion with nil on success or an error message; the
    /// ClaudeProcess protocol says any thread is fine — AppState marshals it.
    static func restart(appURL: URL, completion: @escaping (String?) -> Void) {
        guard FileManager.default.fileExists(atPath: appURL.path) else {
            completion(ClaudeSignature.notFoundMessage(path: appURL.path))
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
            return ClaudeSignature.uninspectableMessage(name: appURL.lastPathComponent)
        }
        var compiled: SecRequirement?
        guard SecRequirementCreateWithString(ClaudeSignature.requirement as CFString, [], &compiled) == errSecSuccess,
              let requirement = compiled else {
            return ClaudeSignature.requirementCompileFailure
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
        return ClaudeSignature.refusalMessage(name: appURL.lastPathComponent, detail: detail)
    }

    /// Runs on the background queue `restart` dispatched to; only the actual
    /// `terminate()` calls are pushed onto main, as AppKit expects.
    private static func terminateAndRelaunch(appURL: URL, completion: @escaping (String?) -> Void) {
        DispatchQueue.main.sync {
            NSRunningApplication.runningApplications(withBundleIdentifier: ClaudeSignature.bundleID)
                .forEach { $0.terminate() }
        }

        let deadline = Date().addingTimeInterval(quitTimeout)
        while Date() < deadline {
            let still = NSRunningApplication.runningApplications(
                withBundleIdentifier: ClaudeSignature.bundleID)
            if still.allSatisfy(\.isTerminated) || still.isEmpty { break }
            Thread.sleep(forTimeInterval: pollInterval)
        }
        let stillRunning = !NSRunningApplication.runningApplications(
            withBundleIdentifier: ClaudeSignature.bundleID).isEmpty
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

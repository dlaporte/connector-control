import Foundation

/// Resolves the four tools the way Claude Desktop would — on the PATH this
/// process was launched with, plus the PATH floor Claude Desktop adds for
/// itself — and, because neither list holds a version manager's directory,
/// falls back to the login shell's PATH to tell "not installed" from
/// "installed where Claude Desktop cannot see it" (spec §3.2). Never throws;
/// the version call is best-effort with a timeout.
public struct ToolProbe: Sendable {
    public static let defaultVersionTimeout: TimeInterval = 2
    public static let defaultShellTimeout: TimeInterval = 2

    /// The directories Claude Desktop's bundle adds to its own PATH (spec
    /// §3.2.3). A tool in one of these counts as visible even when this app's
    /// PATH does not name it; whether Claude's connector spawner reads the
    /// list is unverified, which is why the note says "may not".
    public static let defaultClaudePathFloor = ["/usr/local/bin", "/opt/homebrew/bin", "/opt/homebrew/sbin"]

    public var environment: [String: String]
    /// The login shell's PATH, or nil when it cannot be read. Injected so tests
    /// never spawn a shell.
    public var shellPath: @Sendable () -> String?
    /// Injected so tests can point the floor at their own temp directories.
    public var claudePathFloor: [String]
    public var versionTimeout: TimeInterval

    public init(environment: [String: String],
                shellPath: @escaping @Sendable () -> String?,
                claudePathFloor: [String] = ToolProbe.defaultClaudePathFloor,
                versionTimeout: TimeInterval = ToolProbe.defaultVersionTimeout) {
        self.environment = environment
        self.shellPath = shellPath
        self.claudePathFloor = claudePathFloor
        self.versionTimeout = versionTimeout
    }

    /// The real thing: this process's environment, Claude Desktop's own PATH
    /// floor, and `$SHELL -lc` for the fallback.
    public static func live(
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> ToolProbe {
        let shell = environment["SHELL"] ?? "/bin/zsh"
        return ToolProbe(environment: environment, shellPath: {
            loginShellPath(shell: shell, timeout: defaultShellTimeout)
        })
    }

    public func probe(_ tool: Tool) -> ToolStatus {
        probe([tool])[tool] ?? .notFound
    }

    /// Probes several tools at once; the login shell is consulted at most once
    /// per batch, and only when something is missing from everywhere Claude
    /// Desktop looks.
    public func probe(_ tools: [Tool]) -> [Tool: ToolStatus] {
        var results: [Tool: ToolStatus] = [:]
        var missing: [Tool] = []
        // Where Claude Desktop looks: this app's PATH, then Claude's own floor.
        let visiblePath = ([environment["PATH"] ?? ""] + claudePathFloor).joined(separator: ":")
        for tool in tools {
            if let path = ToolProbe.resolve(tool.name, searchPath: visiblePath) {
                results[tool] = .found(path: path, version: version(at: path, tool: tool))
            } else {
                missing.append(tool)
            }
        }
        guard !missing.isEmpty else { return results }
        let shellSearchPath = shellPath()
        for tool in missing {
            if let shellSearchPath,
               let path = ToolProbe.resolve(tool.name, searchPath: shellSearchPath) {
                results[tool] = .foundInShellOnly(path: path, version: version(at: path, tool: tool))
            } else {
                results[tool] = .notFound
            }
        }
        return results
    }

    /// First executable regular file named `name` in the colon-separated `searchPath`.
    public static func resolve(_ name: String, searchPath: String) -> String? {
        let fm = FileManager.default
        for dir in searchPath.split(separator: ":", omittingEmptySubsequences: true) {
            let candidate = (String(dir) as NSString).appendingPathComponent(name)
            var isDirectory: ObjCBool = false
            if fm.fileExists(atPath: candidate, isDirectory: &isDirectory),
               !isDirectory.boolValue,
               fm.isExecutableFile(atPath: candidate) {
                return candidate
            }
        }
        return nil
    }

    /// First non-blank line of `--version` output, with the tool's own name
    /// prefix (`uv 0.4.30 …`) and one leading `v` before a digit stripped;
    /// nil when there is nothing usable.
    public static func parseVersion(_ output: String, tool: Tool) -> String? {
        let lines = output.split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces) }
        guard let line = lines.first(where: { !$0.isEmpty }) else { return nil }
        var text = Substring(line)
        let prefix = tool.name + " "
        if text.lowercased().hasPrefix(prefix) {
            text = text.dropFirst(prefix.count)
        }
        guard var token = text.split(whereSeparator: \.isWhitespace).first else { return nil }
        if let first = token.first, first == "v" || first == "V",
           let second = token.dropFirst().first, second.isNumber {
            token = token.dropFirst()
        }
        return token.isEmpty ? nil : String(token)
    }

    private func version(at path: String, tool: Tool) -> String? {
        guard let output = ToolProbe.run(path, arguments: ["--version"], timeout: versionTimeout)
        else { return nil }
        return ToolProbe.parseVersion(output, tool: tool)
    }

    /// The login shell's PATH: `$SHELL -lc 'printf %s "$PATH"'`.
    static func loginShellPath(shell: String, timeout: TimeInterval) -> String? {
        guard let output = run(shell, arguments: ["-lc", "printf %s \"$PATH\""], timeout: timeout)
        else { return nil }
        let path = output.trimmingCharacters(in: .whitespacesAndNewlines)
        return path.isEmpty ? nil : path
    }

    /// Runs `executable arguments` with stdin closed and stderr discarded and
    /// returns its stdout; nil when it cannot start or has not finished within
    /// `timeout` (it is then terminated and abandoned).
    static func run(_ executable: String, arguments: [String], timeout: TimeInterval) -> String? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice
        process.standardInput = FileHandle.nullDevice
        do { try process.run() } catch { return nil }
        let output = OutputBox()
        let reader = DispatchGroup()
        reader.enter()
        DispatchQueue.global(qos: .utility).async {
            output.data = pipe.fileHandleForReading.readDataToEndOfFile()
            reader.leave()
        }
        if reader.wait(timeout: .now() + timeout) == .timedOut {
            process.terminate()
            return nil
        }
        process.waitUntilExit()
        return String(decoding: output.data, as: UTF8.self)
    }
}

/// Lets the reader thread hand its bytes back across a `@Sendable` boundary.
private final class OutputBox: @unchecked Sendable {
    var data = Data()
}

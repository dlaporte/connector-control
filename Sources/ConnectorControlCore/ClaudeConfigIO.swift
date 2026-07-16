import Foundation

public enum ClaudeConfigError: Error, Equatable {
    case malformed(String)
}

public enum ClaudeConfigIO {
    public static func readMCPServers(at url: URL) throws -> [String: JSONValue] {
        guard let root = try readRootIfPresent(at: url) else { return [:] }
        guard let raw = root["mcpServers"] else { return [:] }
        guard let dict = raw as? [String: Any] else {
            throw ClaudeConfigError.malformed("mcpServers is not a JSON object")
        }
        return try dict.mapValues(JSONValue.init(any:))
    }

    /// Reads the file fresh, replaces ONLY the mcpServers key, preserves every
    /// other key by value, and writes atomically. Missing file → created.
    /// Malformed file → throws; the file is never overwritten blindly.
    public static func write(mcpServers: [String: JSONValue], to url: URL) throws {
        var root = try readRootIfPresent(at: url) ?? [:]
        root["mcpServers"] = mcpServers.mapValues(\.anyValue)
        let data = try JSONSerialization.data(
            withJSONObject: root, options: [.prettyPrinted, .sortedKeys])
        try AtomicFile.write(data, to: url)
    }

    private static func readRootIfPresent(at url: URL) throws -> [String: Any]? {
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        let data = try Data(contentsOf: url)
        // A zero-byte file (crash/truncation artifact) is deliberately treated
        // like a missing file, not malformed JSON: there is nothing in it to
        // preserve, and callers back up before writing. Reads yield no servers,
        // which surfaces the missing-MCPs recovery UI instead of a hard error.
        guard !data.isEmpty else { return [:] }
        let parsed: Any
        do {
            parsed = try JSONSerialization.jsonObject(with: data)
        } catch {
            throw ClaudeConfigError.malformed(error.localizedDescription)
        }
        guard let root = parsed as? [String: Any] else {
            throw ClaudeConfigError.malformed("top level is not a JSON object")
        }
        return root
    }
}

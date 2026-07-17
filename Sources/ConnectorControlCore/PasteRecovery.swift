import Foundation

/// Turns text a user pastes into the JSON editor into a `(name?, config)` pair.
/// People commonly paste a stanza copied straight out of a
/// `claude_desktop_config.json` `mcpServers` block rather than a bare config
/// object, so this tolerates several shapes:
///   • a plain config object        `{"command": …}`
///   • a full wrapper               `{"mcpServers": {"NAME": {…}}}`
///   • a single-entry name wrapper  `{"NAME": {…}}`
///   • a bare property fragment     `"NAME": {…}`
///   • that fragment with a trailing stray `}` (the mcpServers-copy artifact)
public enum PasteRecovery {
    /// Keys that mark an object as a connector CONFIG rather than a
    /// `{name: config}` wrapper.
    private static let configKeys: Set<String> =
        ["command", "args", "env", "url", "type", "headers"]

    /// Returns the recovered connector name (when the paste carried one) and
    /// its config object, or nil when the text can't be interpreted at all.
    public static func recover(_ text: String) -> (name: String?, config: JSONValue)? {
        guard let value = parseTolerant(text) else { return nil }
        return unwrap(value)
    }

    // MARK: parsing

    private static func parseTolerant(_ text: String) -> JSONValue? {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty else { return nil }
        for candidate in candidates(t) {
            if let v = try? JSONValue.parse(Data(candidate.utf8)) { return v }
        }
        return nil
    }

    private static func candidates(_ t: String) -> [String] {
        var out = [t]
        // A leading quote means a bare `"key": value` fragment — not a legal
        // top-level document. Wrap it, and also try after trimming the stray
        // trailing brace(s) left over from copying one entry out of a block.
        if t.first == "\"" {
            out.append("{\(t)}")
            let balanced = trimStrayTrailingBraces(t)
            if balanced != t { out.append("{\(balanced)}") }
        }
        return out
    }

    /// Drops closing braces at the end of a fragment that exceed its own
    /// opening count (string/escape-aware, so braces inside string values are
    /// ignored). Leaves a fragment that, once wrapped in one `{ }`, balances.
    private static func trimStrayTrailingBraces(_ s: String) -> String {
        var depth = 0, inString = false, escaped = false
        for ch in s {
            if escaped { escaped = false; continue }
            if inString {
                if ch == "\\" { escaped = true }
                else if ch == "\"" { inString = false }
                continue
            }
            switch ch {
            case "\"": inString = true
            case "{": depth += 1
            case "}": depth -= 1
            default: break
            }
        }
        guard depth < 0 else { return s }
        var chars = Array(s)
        while depth < 0 {
            while let last = chars.last, last.isWhitespace { chars.removeLast() }
            guard chars.last == "}" else { break }
            chars.removeLast()
            depth += 1
        }
        return String(chars)
    }

    // MARK: unwrapping

    private static func unwrap(_ value: JSONValue) -> (name: String?, config: JSONValue) {
        // {"mcpServers": {"NAME": {…}}} — single entry.
        if case .object(let outer) = value, outer.count == 1,
           case .object(let inner)? = outer["mcpServers"], inner.count == 1,
           let entry = inner.first {
            return (entry.key, entry.value)
        }
        // {"NAME": {config}} where NAME is neither a config field nor the
        // mcpServers wrapper key.
        if case .object(let outer) = value, outer.count == 1,
           let entry = outer.first, case .object = entry.value,
           entry.key != "mcpServers", !configKeys.contains(entry.key) {
            return (entry.key, entry.value)
        }
        return (nil, value)
    }
}

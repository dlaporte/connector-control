import Foundation

/// Which of the four tools a connector's command needs (spec §3.3): the first
/// token by basename, case-insensitive, `.cmd`/`.exe` stripped, one `cmd /c`
/// unwrapped. A command written as a path (`/usr/local/bin/npx`) is left
/// alone — the user chose it deliberately and PATH lookup does not apply.
public enum ToolRequirement {
    public static func requiredTool(command: String, args: [String]) -> Tool? {
        guard let first = normalized(command) else { return nil }
        if first == "cmd", args.count >= 2, args[0].lowercased() == "/c" {
            guard let inner = normalized(args[1]) else { return nil }
            return Tool(name: inner)
        }
        return Tool(name: first)
    }

    /// The rule applied to a config object's `command` and string `args`
    /// (any non-string arg empties the list). Non-objects → nil.
    public static func requiredTool(for config: JSONValue) -> Tool? {
        guard case .object(let object) = config,
              case .string(let command)? = object["command"] else { return nil }
        var args: [String] = []
        if case .array(let raw)? = object["args"] {
            for item in raw {
                guard case .string(let s) = item else { args = []; break }
                args.append(s)
            }
        }
        return requiredTool(command: command, args: args)
    }

    /// Lower-cased basename without one trailing `.cmd`/`.exe`; nil for blank
    /// or path-like tokens.
    static func normalized(_ token: String) -> String? {
        let trimmed = token.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty, !trimmed.contains("/"), !trimmed.contains("\\") else { return nil }
        var name = trimmed.lowercased()
        for ext in [".cmd", ".exe"] where name.hasSuffix(ext) {
            name.removeLast(ext.count)
            break
        }
        return name
    }
}

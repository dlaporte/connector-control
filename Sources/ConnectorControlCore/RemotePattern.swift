import Foundation

/// Recognizes the `npx [-y] mcp-remote <url>` bridge pattern so the form view can
/// show just Name + Server URL. Keys other than command/args (env, headers, …)
/// don't disqualify — they surface in the form's read-only Additional fields.
public enum RemotePattern {
    public static func detect(_ config: JSONValue) -> String? {
        guard case .object(let object) = config,
              case .string("npx") = object["command"] ?? .null,
              case .array(let rawArgs) = object["args"] ?? .null
        else { return nil }
        var args: [String] = []
        for raw in rawArgs {
            guard case .string(let s) = raw else { return nil }
            args.append(s)
        }
        if args.first == "-y" { args.removeFirst() }
        guard args.count == 2, args[0] == "mcp-remote" else { return nil }
        guard let url = URL(string: args[1]), let scheme = url.scheme?.lowercased(),
              scheme == "http" || scheme == "https", url.host != nil
        else { return nil }
        return args[1]
    }

    public static func make(url: String) -> JSONValue {
        .object(["command": .string("npx"),
                 "args": .array([.string("-y"), .string("mcp-remote"), .string(url)])])
    }

    /// True when the config is an `npx [-y] mcp-remote …` invocation, regardless
    /// of whether the URL argument is valid. Used to keep the remote form active
    /// and to block saves of remote-shaped configs whose URL doesn't validate.
    public static func isRemoteShaped(_ config: JSONValue) -> Bool {
        guard case .object(let object) = config,
              case .string("npx") = object["command"] ?? .null,
              case .array(let rawArgs) = object["args"] ?? .null
        else { return false }
        var args: [String] = []
        for raw in rawArgs {
            guard case .string(let s) = raw else { return false }
            args.append(s)
        }
        if args.first == "-y" { args.removeFirst() }
        return args.first == "mcp-remote"
    }
}

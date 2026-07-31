import Foundation

/// Recognizes the `npx [-y] mcp-remote[@version] <url>` bridge pattern so the
/// form view can show just Name + Server URL. Keys other than command/args
/// (env, headers, …) don't disqualify — they surface in the form's read-only
/// Additional fields.
public enum RemotePattern {
    /// True when `s` is the mcp-remote bridge package specifier, with or
    /// without a version tag (e.g. `mcp-remote@latest`).
    static func isMarker(_ s: String) -> Bool {
        s == "mcp-remote" || s.hasPrefix("mcp-remote@")
    }

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
        guard args.count == 2, isMarker(args[0]) else { return nil }
        guard let url = URL(string: args[1]), let scheme = url.scheme?.lowercased(),
              scheme == "http" || scheme == "https", url.host != nil
        else { return nil }
        return args[1]
    }

    public static func make(url: String) -> JSONValue {
        .object(["command": .string("npx"),
                 "args": .array([.string("-y"), .string("mcp-remote"), .string(url)])])
    }

    /// True when the config is an `npx [-y] mcp-remote… …` invocation, regardless
    /// of whether the URL argument is valid. Used to keep the remote form active
    /// for forced-remote targets.
    public static func isRemoteShaped(_ config: JSONValue) -> Bool {
        strippedArgs(config)?.first.map(isMarker) ?? false
    }

    /// True for a BARE bridge invocation — `npx [-y] mcp-remote…` with at most
    /// one trailing argument (the URL slot, present or missing). These must
    /// carry a valid URL to be saveable. Extra flags (e.g. --header) make a
    /// config non-bare: still remote-shaped, but save validation must not
    /// insist the trailing args form a lone URL.
    public static func isCanonicalShape(_ config: JSONValue) -> Bool {
        guard let args = strippedArgs(config) else { return false }
        return args.count <= 2 && (args.first.map(isMarker) ?? false)
    }

    /// String args with a leading "-y" stripped, or nil when the config isn't
    /// an all-string-args npx invocation.
    private static func strippedArgs(_ config: JSONValue) -> [String]? {
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
        return args
    }
}

// MARK: - Remote authentication

/// How mcp-remote should authenticate to the remote server.
public enum RemoteAuth: Equatable {
    /// OAuth dynamic client registration, or no auth for an open server.
    case automatic
    /// `Authorization: Bearer <token>`.
    case bearer(token: String)
    /// An arbitrary static header.
    case header(name: String, value: String)
    /// A pre-registered OAuth client (id/secret, optional space-separated scopes).
    case oauthClient(clientID: String, clientSecret: String, scopes: String)
}

/// The pieces of a `npx mcp-remote` invocation the Remote form edits, plus
/// whatever it doesn't model so those survive a round-trip untouched.
public struct RemoteConfig: Equatable {
    public var url: String
    public var auth: RemoteAuth
    /// mcp-remote flags this app has no widget for — preserved verbatim, in order.
    public var extraArgs: [String]
    /// Env vars other than the one auth uses to indirect a header value.
    public var passthroughEnv: [String: String]

    public init(url: String, auth: RemoteAuth,
                extraArgs: [String] = [], passthroughEnv: [String: String] = [:]) {
        self.url = url
        self.auth = auth
        self.extraArgs = extraArgs
        self.passthroughEnv = passthroughEnv
    }
}

public extension RemotePattern {
    /// Builds the full `npx mcp-remote` config for a `RemoteConfig`, encoding
    /// `auth` into the flags/env mcp-remote expects.
    static func encode(_ r: RemoteConfig) -> JSONValue {
        var args: [String] = ["-y", "mcp-remote", r.url]
        args.append(contentsOf: r.extraArgs)
        var env = r.passthroughEnv

        switch r.auth {
        case .automatic:
            break
        case .bearer(let token):
            // Space-safe workaround: no space around the colon in the arg
            // itself — the space lives in the env value instead.
            args += ["--header", "Authorization:${AUTH_HEADER}"]
            env["AUTH_HEADER"] = "Bearer \(token)"
        case .header(let name, let value):
            args += ["--header", "\(name):${AUTH_HEADER}"]
            env["AUTH_HEADER"] = value
        case .oauthClient(let clientID, let clientSecret, let scopes):
            // Literal id/secret in the arg: mcp-remote reads client info from
            // this flag directly, and env indirection isn't reliably supported
            // for it.
            args += ["--static-oauth-client-info",
                     compactJSON(["client_id": clientID, "client_secret": clientSecret])]
            if !scopes.isEmpty {
                args += ["--static-oauth-client-metadata", compactJSON(["scope": scopes])]
            }
        }

        var object: [String: JSONValue] = [
            "command": .string("npx"),
            "args": .array(args.map(JSONValue.string)),
        ]
        if !env.isEmpty {
            object["env"] = .object(env.mapValues(JSONValue.string))
        }
        return .object(object)
    }

    /// Reads a `npx mcp-remote` config back into a `RemoteConfig`, or nil when
    /// it isn't one (wrong command, missing/invalid marker, or no valid URL).
    static func decode(_ config: JSONValue) -> RemoteConfig? {
        guard case .object(let object) = config,
              case .string("npx") = object["command"] ?? .null,
              case .array(let rawArgs) = object["args"] ?? .null
        else { return nil }
        var args: [String] = []
        for raw in rawArgs {
            guard case .string(let s) = raw else { return nil }
            args.append(s)
        }

        var i = 0
        if args.first == "-y" { i = 1 }
        guard i < args.count, isMarker(args[i]) else { return nil }
        i += 1
        guard i < args.count else { return nil }
        let urlString = args[i]
        guard let url = URL(string: urlString), let scheme = url.scheme?.lowercased(),
              scheme == "http" || scheme == "https", url.host != nil
        else { return nil }
        i += 1

        var env: [String: String] = [:]
        if case .object(let envObject)? = object["env"] {
            for (key, value) in envObject {
                if case .string(let s) = value { env[key] = s }
            }
        }

        var extraArgs: [String] = []
        var consumedEnvKeys: Set<String> = []
        var headerName: String?
        var headerValue: String?
        var clientID: String?
        var clientSecret: String?
        var scopes: String?

        while i < args.count {
            let arg = args[i]
            switch arg {
            case "--header" where i + 1 < args.count:
                let raw = args[i + 1]
                // Recognize ONLY this app's own indirection sentinel —
                // `Name:${AUTH_HEADER}` backed by an AUTH_HEADER env var — as the
                // editable auth header, and only the first one. Every other
                // --header (literal values, other env vars, additional headers)
                // is preserved verbatim as an extra arg so nothing is ever lost
                // or collides with our fixed AUTH_HEADER slot on re-encode.
                if headerName == nil,
                   let colon = raw.firstIndex(of: ":"),
                   raw[raw.index(after: colon)...] == "${AUTH_HEADER}",
                   let value = env["AUTH_HEADER"] {
                    headerName = String(raw[raw.startIndex..<colon])
                    headerValue = value
                    consumedEnvKeys.insert("AUTH_HEADER")
                } else {
                    extraArgs.append(arg)
                    extraArgs.append(raw)
                }
                i += 2
            case "--static-oauth-client-info" where i + 1 < args.count:
                if let obj = parseJSONObject(args[i + 1]) {
                    clientID = obj["client_id"] as? String
                    clientSecret = obj["client_secret"] as? String
                }
                i += 2
            case "--static-oauth-client-metadata" where i + 1 < args.count:
                if let obj = parseJSONObject(args[i + 1]) {
                    scopes = obj["scope"] as? String
                }
                i += 2
            default:
                extraArgs.append(arg)
                i += 1
            }
        }

        var auth: RemoteAuth = .automatic
        if let clientID {
            auth = .oauthClient(clientID: clientID, clientSecret: clientSecret ?? "",
                                 scopes: scopes ?? "")
        } else if let headerName, let headerValue {
            if headerName == "Authorization", headerValue.hasPrefix("Bearer ") {
                auth = .bearer(token: String(headerValue.dropFirst("Bearer ".count)))
            } else {
                auth = .header(name: headerName, value: headerValue)
            }
        }

        for key in consumedEnvKeys { env.removeValue(forKey: key) }

        return RemoteConfig(url: urlString, auth: auth,
                             extraArgs: extraArgs, passthroughEnv: env)
    }

    /// Compact (no whitespace), key-sorted JSON — matches what mcp-remote
    /// expects in a single CLI argument.
    private static func compactJSON(_ dict: [String: String]) -> String {
        let data = (try? JSONSerialization.data(withJSONObject: dict, options: [.sortedKeys])) ?? Data()
        return String(decoding: data, as: UTF8.self)
    }

    private static func parseJSONObject(_ text: String) -> [String: Any]? {
        guard let data = text.data(using: .utf8) else { return nil }
        return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
    }
}

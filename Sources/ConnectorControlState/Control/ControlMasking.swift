import ConnectorControlCore

/// Mirror: windows/src/ConnectorControl.Core/State/Control/ControlMasking.cs
///
/// What a connector looks like to anyone but its owner: the command line and URL in full, with
/// every secret value replaced. Environment and header values are never shown, only whether they
/// are set, and nor is a value under a key named for a secret; an argument that reads as a
/// credential, the value after a flag named for a secret, a `NAME=value` word's value when the
/// name is a secret's, a header's value, a URL's password and query and fragment values, and a
/// URL's user name or path segment that reads as a credential, read "•••". Placeholders —
/// `${CC_NEEDS:…}` and `${COLLECTION_DIR}` — name what is missing rather than what is secret, and
/// show as written when they are the whole value. Every rule is plain string work, with no URL
/// parser, so both platforms mask the same text the same way. `reveal` (the command-line tool's
/// `show --reveal`, never Claude's) turns it all off.
/// Main-actor only: it reads `EditorModel.needsValue` and `CollectionsModel.remoteType`, which are.
@MainActor
public enum ControlMasking {
    public static let mask = "•••"
    /// A secret's state in structured output.
    public static let setState = "set"
    public static let needsValueState = "needs value"
    /// A set secret on a code line; an unset one reads `EditorModel.needsValue`.
    public static let setText = "••• (set)"
    public static let typeLabel = "type"
    public static let urlLabel = "url"
    public static let headersLabel = "headers"
    public static let oauthLabel = "oauth"
    public static let scopesLabel = "scopes"
    public static let commandLabel = "command"
    public static let argsLabel = "args"
    public static let envLabel = "env"
    public static let otherLabel = "other"
    public static let nameLabel = "name"
    public static let removedPrefix = "  − "
    public static let addedPrefix = "  + "

    public static func remoteBridgeType(_ package: String) -> String { "remote, through \(package)" }

    public static func oauthClientText(_ clientID: String, _ secret: String) -> String { "client \(clientID), secret \(secret)" }

    public static func assignment(_ name: String, _ value: String) -> String { "\(name) = \(value)" }

    // MARK: Values

    /// A value that is wholly a reference — a placeholder, `${COLLECTION_DIR}`, or a `${NAME}` /
    /// `$NAME` — takes its secret from elsewhere and holds none itself. One with a placeholder
    /// inside other text can still hold a secret beside it, so it is masked like any other.
    static func refersElsewhere(_ value: String) -> Bool {
        CredentialHeuristics.isReference(value)
    }

    /// Whether `text` reads as a credential or a random token, as written or with every `${…}`
    /// taken out: the heuristics take a placeholder for a value still to come, so
    /// `sk-live-…${CC_NEEDS:x}` would otherwise pass.
    static func looksSecret(_ text: String) -> Bool {
        [text, withoutReferences(text)].contains {
            CredentialHeuristics.looksLikeCredential($0) || CredentialHeuristics.looksLikeRandomToken($0)
        }
    }

    /// Whether a piece of a URL is a secret: it reads as one and is not wholly a reference.
    static func secretShaped(_ text: String) -> Bool {
        !refersElsewhere(text) && looksSecret(text)
    }

    /// `text` with every `${…}` taken out.
    static func withoutReferences(_ text: String) -> String {
        let scalars = Array(text.unicodeScalars)
        var kept: [Unicode.Scalar] = []
        var index = 0
        while index < scalars.count {
            if scalars[index] == "$", index + 1 < scalars.count, scalars[index + 1] == "{",
               let close = scalars[(index + 2)...].firstIndex(of: "}") {
                index = close + 1
            } else {
                kept.append(scalars[index])
                index += 1
            }
        }
        return string(kept[...])
    }

    /// "needs value" for an empty value or one still holding a `${CC_NEEDS:…}` marker; else "set".
    public static func secretState(_ value: String) -> String {
        value.isEmpty || Placeholder.containsMarker(value) ? needsValueState : setState
    }

    /// A secret on a code line: its value with `reveal`, else "••• (set)" or "needs your value".
    public static func secretText(_ value: String, reveal: Bool = false) -> String {
        if reveal { return value }
        return secretState(value) == setState ? setText : EditorModel.needsValue
    }

    // MARK: Cutting text

    // Platform idiom: text is cut scalar by scalar here and by UTF-16 unit on Windows, which cut
    // in the same places; a Swift `Character` would carry a combining mark after a delimiter
    // along with it and cut elsewhere.

    /// The text before and after the first `delimiter`, or nil when there is none.
    static func cut(_ text: String, at delimiter: String) -> (before: String, after: String)? {
        let scalars = Array(text.unicodeScalars)
        let mark = Array(delimiter.unicodeScalars)
        guard scalars.count >= mark.count else { return nil }
        for index in 0...(scalars.count - mark.count) where Array(scalars[index..<(index + mark.count)]) == mark {
            return (string(scalars[..<index]), string(scalars[(index + mark.count)...]))
        }
        return nil
    }

    /// The text before and after the last `delimiter`, or nil when there is none.
    static func cutLast(_ text: String, at delimiter: Unicode.Scalar) -> (before: String, after: String)? {
        let scalars = Array(text.unicodeScalars)
        guard let index = scalars.lastIndex(of: delimiter) else { return nil }
        return (string(scalars[..<index]), string(scalars[(index + 1)...]))
    }

    /// `text` in the pieces between its `delimiter`s, empty ones included.
    static func pieces(_ text: String, by delimiter: Unicode.Scalar) -> [String] {
        Array(text.unicodeScalars).split(separator: delimiter, omittingEmptySubsequences: false).map(string)
    }

    /// Whether `text` starts with "-".
    static func isFlag(_ text: String) -> Bool {
        text.unicodeScalars.first == "-"
    }

    static func string(_ scalars: ArraySlice<Unicode.Scalar>) -> String {
        var text = String.UnicodeScalarView()
        text.append(contentsOf: scalars)
        return String(text)
    }

    // MARK: Arguments and URLs

    /// `text` with a URL's password and every query and fragment value replaced by "•••", and
    /// so its user name and any path segment that reads as a credential or a random token;
    /// anything that is not `scheme://…` comes back as it is, and so does a value that is wholly
    /// a reference.
    public static func url(_ text: String, reveal: Bool = false) -> String {
        guard !reveal, let scheme = cut(text, at: "://") else { return text }
        var rest = scheme.after
        var fragment: String?
        if let piece = cut(rest, at: "#") {
            rest = piece.before
            fragment = piece.after
        }
        var query: String?
        if let piece = cut(rest, at: "?") {
            rest = piece.before
            query = piece.after
        }
        var authority = rest
        var path: String?
        if let piece = cut(rest, at: "/") {
            authority = piece.before
            path = piece.after
        }
        if let piece = cutLast(authority, at: "@") {
            authority = userInfo(piece.before) + "@" + piece.after
        }
        var result = scheme.before + "://" + authority
        if let path {
            result += "/" + pieces(path, by: "/").map { secretShaped($0) ? mask : $0 }.joined(separator: "/")
        }
        if let query { result += "?" + pairs(query) }
        if let fragment { result += "#" + pairs(fragment) }
        return result
    }

    /// A URL's `user:password`: the password masked unless it is empty or a reference, and the
    /// user name when it reads as a credential or a random token.
    static func userInfo(_ text: String) -> String {
        guard let piece = cut(text, at: ":") else { return secretShaped(text) ? mask : text }
        let password = piece.after.isEmpty || refersElsewhere(piece.after) ? piece.after : mask
        return (secretShaped(piece.before) ? mask : piece.before) + ":" + password
    }

    /// A query's or a fragment's `name=value&…`: each value masked unless it is empty or a
    /// reference, and a piece with no "=" when it reads as a credential or a random token.
    static func pairs(_ text: String) -> String {
        pieces(text, by: "&").map { pair in
            guard let piece = cut(pair, at: "=") else { return secretShaped(pair) ? mask : pair }
            return piece.after.isEmpty || refersElsewhere(piece.after) ? pair : piece.before + "=" + mask
        }.joined(separator: "&")
    }

    /// One argument on its own: a URL through `url(_:)`, a value that is wholly a reference as
    /// written, `name=value` with the value masked when the name is a secret's (`--api-key=…`,
    /// Docker's `-e API_KEY=…`), and "•••" for anything that reads as a credential or a random
    /// token — a "name" that does is part of the token, so the whole word is masked.
    public static func argument(_ arg: String, reveal: Bool = false) -> String {
        if reveal { return arg }
        if cut(arg, at: "://") != nil { return url(arg) }
        if refersElsewhere(arg) { return arg }
        if let piece = cut(arg, at: "="), !looksSecret(piece.before) {
            if CredentialHeuristics.namesASecret(piece.before), !refersElsewhere(piece.after) {
                return piece.before + "=" + mask
            }
            return piece.before + "=" + argument(piece.after)
        }
        return looksSecret(arg) ? mask : arg
    }

    /// An argument list: each through `argument(_:)`, and also the value after a flag named for a
    /// secret (`--api-key x`, `--static-oauth-client-info {…}`) unless it is itself a flag, and a
    /// header after `--header` or `-H`.
    public static func arguments(_ args: [String], reveal: Bool = false) -> [String] {
        guard !reveal else { return args }
        return args.enumerated().map { index, arg in
            if index > 0 {
                let previous = args[index - 1]
                if previous == "--header" || previous == "-H" { return header(arg) }
                if isFlag(previous), cut(previous, at: "=") == nil, CredentialHeuristics.namesASecret(previous),
                   !isFlag(arg), !refersElsewhere(arg) {
                    return mask
                }
            }
            return argument(arg)
        }
    }

    /// "Name: value" with the value masked unless it is empty or a reference.
    static func header(_ text: String) -> String {
        guard let piece = cut(text, at: ":") else { return argument(text) }
        let value = piece.after.trimmingCharacters(in: .whitespaces)
        if value.isEmpty || refersElsewhere(value) { return text }
        return piece.before + ": " + mask
    }

    // MARK: Whole configs

    /// The keys whose values are secrets by name: every value under them is.
    static let secretKeys: Set<String> = ["env", "headers"]

    /// The config as structured output carries it, in its own shape: `env` and `headers` values
    /// become "set" or "needs value", and so does every string or number under a key named for a
    /// secret; an array is an argument list through `arguments(_:)`, and every other string goes
    /// through `argument(_:)`, a URL through `url(_:)`.
    public static func maskedConfig(_ config: JSONValue, reveal: Bool = false) -> JSONValue {
        guard !reveal else { return config }
        guard case .object(let object) = config else { return maskedLeaves(config, secret: false) }
        var out: [String: JSONValue] = [:]
        for (key, value) in object {
            if secretKeys.contains(key), case .object(let values) = value {
                out[key] = .object(values.mapValues { item in
                    if case .string(let text) = item { return .string(secretState(text)) }
                    return .string(setState)
                })
            } else {
                out[key] = maskedLeaves(value, secret: secretKeys.contains(key) || CredentialHeuristics.namesASecret(key))
            }
        }
        return .object(out)
    }

    /// `value` masked: under a key named for a secret (`secret`) every string or number reads
    /// "set" or "needs value"; elsewhere an array is an argument list, its strings masked as
    /// `arguments(_:)` masks them, and every other string goes through `argument(_:)`.
    static func maskedLeaves(_ value: JSONValue, secret: Bool) -> JSONValue {
        switch value {
        case .string(let text):
            return .string(secret ? secretState(text) : argument(text))
        case .int, .double:
            return secret ? .string(setState) : value
        case .array(let items):
            if secret { return .array(items.map { maskedLeaves($0, secret: true) }) }
            let shown = arguments(items.map { item -> String in
                if case .string(let text) = item { return text }
                return ""
            })
            return .array(zip(items, shown).map { item, text in
                if case .string = item { return .string(text) }
                return maskedLeaves(item, secret: false)
            })
        case .object(let object):
            var out: [String: JSONValue] = [:]
            for (key, item) in object {
                out[key] = maskedLeaves(item, secret: secret || CredentialHeuristics.namesASecret(key))
            }
            return .object(out)
        case .null, .bool:
            return value
        }
    }

    /// One field of a config as a person reads it: its label, and each value as written (`raw`,
    /// which tells a changed secret from an unchanged one) and as shown.
    struct Field {
        let label: String
        var values: [(raw: String, shown: String)]
    }

    /// What a connector runs, one field per line, for `get_connector`'s text and the confirmation
    /// dialogs: a connector reached through mcp-remote by its URL and how it signs in; one reached
    /// by URL alone by its URL and headers; anything else by its command, arguments and environment.
    public static func codeLines(_ config: JSONValue, reveal: Bool = false) -> [String] {
        ControlText.labelled(fields(config, reveal: reveal).flatMap { field in
            field.values.enumerated().map { index, value in (index == 0 ? field.label : "", value.shown) }
        })
    }

    /// The fields an edit changes, for the change confirmation: each changed field's label, then
    /// "− " before each value it loses and "+ " before each it gains. A secret that changed shows
    /// as changed although both sides read "••• (set)".
    public static func changeLines(from before: JSONValue, to after: JSONValue) -> [String] {
        let old = fields(before, reveal: false)
        let new = fields(after, reveal: false)
        var labels: [String] = []
        for field in new + old where !labels.contains(field.label) { labels.append(field.label) }
        var lines: [String] = []
        for label in labels {
            let was = old.first { $0.label == label }?.values ?? []
            let now = new.first { $0.label == label }?.values ?? []
            let lost = was.filter { item in !now.contains { $0.raw == item.raw && $0.shown == item.shown } }
            let gained = now.filter { item in !was.contains { $0.raw == item.raw && $0.shown == item.shown } }
            guard !lost.isEmpty || !gained.isEmpty else { continue }
            lines.append(label)
            lines += lost.map { removedPrefix + $0.shown }
            lines += gained.map { addedPrefix + $0.shown }
        }
        return lines
    }

    static func fields(_ config: JSONValue, reveal: Bool) -> [Field] {
        if let remote = RemotePattern.decode(config) {
            var fields = [Field(label: typeLabel, values: [(remote.package, remoteBridgeType(remote.package))]),
                          Field(label: urlLabel, values: [(remote.url, url(remote.url, reveal: reveal))])]
            switch remote.auth {
            case .automatic:
                break
            case .bearer(let token):
                fields.append(Field(label: headersLabel,
                                    values: [(token, assignment("Authorization", secretText(token, reveal: reveal)))]))
            case .header(let name, let value):
                fields.append(Field(label: headersLabel,
                                    values: [(name + ":" + value, assignment(name, secretText(value, reveal: reveal)))]))
            case .oauthClient(let clientID, let clientSecret, let scopes):
                fields.append(Field(label: oauthLabel,
                                    values: [(clientID + ":" + clientSecret,
                                              oauthClientText(clientID, secretText(clientSecret, reveal: reveal)))]))
                if !scopes.isEmpty { fields.append(Field(label: scopesLabel, values: [(scopes, scopes)])) }
            }
            if !remote.extraArgs.isEmpty {
                fields.append(Field(label: argsLabel, values: [(remote.extraArgs.joined(separator: " "),
                                                                arguments(remote.extraArgs, reveal: reveal).joined(separator: " "))]))
            }
            return fields + envFields(remote.passthroughEnv, reveal: reveal)
        }
        guard case .object(let object) = config else { return [] }
        if object["command"] == nil, case .string(let address)? = object["url"] {
            var fields = [Field(label: typeLabel, values: [("", CollectionsModel.remoteType)]),
                          Field(label: urlLabel, values: [(address, url(address, reveal: reveal))])]
            if case .object(let headers)? = object["headers"] {
                let values = headers.keys.sorted { $0.ordinallyPrecedes($1) }.map { name -> (raw: String, shown: String) in
                    var text = ""
                    if case .string(let value)? = headers[name] { text = value }
                    return (name + ":" + text, assignment(name, secretText(text, reveal: reveal)))
                }
                fields.append(Field(label: headersLabel, values: values))
            }
            return fields
        }
        let model = FormMapper.analyze(config).model
        var fields = [Field(label: commandLabel, values: [(model.command, argument(model.command, reveal: reveal))])]
        if !model.args.isEmpty {
            fields.append(Field(label: argsLabel, values: [(model.args.joined(separator: " "),
                                                            arguments(model.args, reveal: reveal).joined(separator: " "))]))
        }
        fields += envFields(model.env, reveal: reveal)
        if !model.additional.isEmpty {
            let keys = model.additional.keys.sorted { $0.ordinallyPrecedes($1) }.joined(separator: ", ")
            fields.append(Field(label: otherLabel, values: [(keys, keys)]))
        }
        return fields
    }

    static func envFields(_ env: [String: String], reveal: Bool) -> [Field] {
        guard !env.isEmpty else { return [] }
        let values = env.keys.sorted { $0.ordinallyPrecedes($1) }.map { name -> (raw: String, shown: String) in
            let value = env[name] ?? ""
            return (name + "=" + value, assignment(name, secretText(value, reveal: reveal)))
        }
        return [Field(label: envLabel, values: values)]
    }
}

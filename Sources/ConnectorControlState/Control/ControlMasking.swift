import ConnectorControlCore

/// Mirror: windows/src/ConnectorControl.Core/State/Control/ControlMasking.cs
///
/// What a connector looks like to anyone but its owner: the command line and URL in full, with
/// every secret value replaced. Environment and header values are never shown, only whether they
/// are set; an argument that reads as a credential, the value after a flag named for a secret, a
/// header's value and a URL's password and query values read "•••". Placeholders — `${CC_NEEDS:…}`
/// and `${COLLECTION_DIR}` — name what is missing rather than what is secret, and show as written.
/// Every rule is plain string work, with no URL parser, so both platforms mask the same text the
/// same way. `reveal` (the command-line tool's `show --reveal`, never Claude's) turns it all off.
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

    /// A placeholder names what is missing, never what is secret.
    static func isPlaceholder(_ value: String) -> Bool {
        Placeholder.containsMarker(value) || value.contains(Placeholder.directoryToken)
    }

    /// A value that takes its secret from elsewhere — a placeholder, or a `${NAME}` / `$NAME`
    /// reference — holds none itself.
    static func refersElsewhere(_ value: String) -> Bool {
        isPlaceholder(value) || CredentialHeuristics.isReference(value)
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

    /// `text` with a URL's password and every query value replaced by "•••"; anything that is not
    /// `scheme://…` comes back as it is, and so does a value that is a placeholder or a reference.
    /// The fragment is kept as written.
    public static func url(_ text: String, reveal: Bool = false) -> String {
        guard !reveal, let schemeEnd = text.range(of: "://") else { return text }
        var rest = String(text[schemeEnd.upperBound...])
        var fragment = ""
        if let hash = rest.firstIndex(of: "#") {
            fragment = String(rest[hash...])
            rest = String(rest[..<hash])
        }
        var query: String?
        if let question = rest.firstIndex(of: "?") {
            query = String(rest[rest.index(after: question)...])
            rest = String(rest[..<question])
        }
        let slash = rest.firstIndex(of: "/") ?? rest.endIndex
        var authority = String(rest[..<slash])
        let path = String(rest[slash...])
        if let at = authority.lastIndex(of: "@") {
            let userInfo = String(authority[..<at])
            if let colon = userInfo.firstIndex(of: ":") {
                let password = String(userInfo[userInfo.index(after: colon)...])
                if !password.isEmpty, !refersElsewhere(password) {
                    authority = String(userInfo[...colon]) + mask + String(authority[at...])
                }
            }
        }
        var result = String(text[..<schemeEnd.upperBound]) + authority + path
        if let query {
            let pairs = query.split(separator: "&", omittingEmptySubsequences: false).map { pair -> String in
                let parts = pair.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
                if parts.count == 2 {
                    let value = String(parts[1])
                    return value.isEmpty || refersElsewhere(value) ? String(pair) : String(parts[0]) + "=" + mask
                }
                let bare = String(pair)
                return CredentialHeuristics.looksLikeCredential(bare) || CredentialHeuristics.looksLikeRandomToken(bare) ? mask : bare
            }
            result += "?" + pairs.joined(separator: "&")
        }
        return result + fragment
    }

    /// One argument on its own: a placeholder as written, a URL through `url(_:)`, `--flag=value`
    /// with the value masked when the flag is named for a secret, and "•••" for anything that
    /// reads as a credential or a random token.
    public static func argument(_ arg: String, reveal: Bool = false) -> String {
        if reveal || isPlaceholder(arg) { return arg }
        if arg.contains("://") { return url(arg) }
        if arg.hasPrefix("-"), let equals = arg.firstIndex(of: "=") {
            let flag = String(arg[..<equals])
            let value = String(arg[arg.index(after: equals)...])
            if CredentialHeuristics.namesASecret(flag), !refersElsewhere(value) { return flag + "=" + mask }
            return flag + "=" + argument(value)
        }
        if CredentialHeuristics.looksLikeCredential(arg) || CredentialHeuristics.looksLikeRandomToken(arg) { return mask }
        return arg
    }

    /// An argument list: each through `argument(_:)`, and also the value after a flag named for a
    /// secret (`--api-key x`, `--static-oauth-client-info {…}`) and a header after `--header`.
    public static func arguments(_ args: [String], reveal: Bool = false) -> [String] {
        guard !reveal else { return args }
        return args.enumerated().map { index, arg in
            if index > 0 {
                let previous = args[index - 1]
                if previous == "--header" || previous == "-H" { return header(arg) }
                if previous.hasPrefix("-"), !previous.contains("="), CredentialHeuristics.namesASecret(previous),
                   !refersElsewhere(arg) {
                    return mask
                }
            }
            return argument(arg)
        }
    }

    /// "Name: value" with the value masked unless it refers elsewhere.
    static func header(_ text: String) -> String {
        guard let colon = text.firstIndex(of: ":") else { return argument(text) }
        let value = String(text[text.index(after: colon)...]).trimmingCharacters(in: .whitespaces)
        if value.isEmpty || refersElsewhere(value) { return text }
        return String(text[...colon]) + " " + mask
    }

    // MARK: Whole configs

    /// The config as structured output carries it, in its own shape: `env` and `headers` values
    /// become "set" or "needs value", `args` go through `arguments(_:)`, `url` through `url(_:)`,
    /// and every other string through `argument(_:)`.
    public static func maskedConfig(_ config: JSONValue, reveal: Bool = false) -> JSONValue {
        guard !reveal, case .object(let object) = config else { return config }
        var out: [String: JSONValue] = [:]
        for (key, value) in object {
            switch (key, value) {
            case ("env", .object(let values)), ("headers", .object(let values)):
                out[key] = .object(values.mapValues { item in
                    if case .string(let text) = item { return .string(secretState(text)) }
                    return .string(setState)
                })
            case ("args", .array(let items)):
                let strings = items.compactMap { item -> String? in
                    if case .string(let text) = item { return text }
                    return nil
                }
                out[key] = strings.count == items.count
                    ? .array(arguments(strings).map(JSONValue.string))
                    : .array(items.map(maskedLeaves))
            case ("url", .string(let address)):
                out[key] = .string(url(address))
            default:
                out[key] = maskedLeaves(value)
            }
        }
        return .object(out)
    }

    static func maskedLeaves(_ value: JSONValue) -> JSONValue {
        switch value {
        case .string(let text): return .string(argument(text))
        case .array(let items): return .array(items.map(maskedLeaves))
        case .object(let object): return .object(object.mapValues(maskedLeaves))
        default: return value
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

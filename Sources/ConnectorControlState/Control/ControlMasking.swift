import ConnectorControlCore
import Foundation

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
/// parser, so both platforms mask the same text the same way. Structured output is masked or, for
/// the command-line tool's `show --reveal` (never Claude's), revealed; the line renderers take an
/// `Exposure`, which adds the confirmation dialog's.
/// Main-actor only: it reads `EditorModel.needsValue` and `CollectionsModel.remoteType`, which are.
@MainActor
public enum ControlMasking {
    /// How much a line renderer shows. `masked`: what Claude and the command-line tool see.
    /// `dialog`: the confirmation dialogs, whose text never goes back to Claude — the command,
    /// arguments and URL in full, with environment and header values, the OAuth client secret and
    /// every value under a key named for a secret still masked. `revealed`: everything as written,
    /// for the command-line tool's `show --reveal`.
    public enum Exposure: Sendable { case masked, dialog, revealed }

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
        arguments(args, exposure: reveal ? .revealed : .masked)
    }

    /// An argument list as `exposure` shows it. The dialog keeps every argument as written but a
    /// header's value and the OAuth client secret.
    static func arguments(_ args: [String], exposure: Exposure) -> [String] {
        guard exposure != .revealed else { return args }
        return args.enumerated().map { index, arg in
            if index > 0 {
                let previous = args[index - 1]
                if previous == "--header" || previous == "-H" { return header(arg) }
                if hidesNext(previous, exposure), !isFlag(arg), !refersElsewhere(arg) {
                    return exposure == .dialog ? clientInfo(arg) : mask
                }
            }
            return exposure == .masked ? argument(arg) : dialogArgument(arg)
        }
    }

    /// One argument as the dialog shows it: as written, but for the value of
    /// `--static-oauth-client-info=…`, which holds the client secret.
    static func dialogArgument(_ arg: String) -> String {
        guard let piece = cut(arg, at: "="), hidesNext(piece.before, .dialog), !refersElsewhere(piece.after) else { return arg }
        return piece.before + "=" + clientInfo(piece.after)
    }

    /// The OAuth client info as the dialog shows it: its JSON on one line with the client secret,
    /// and any value under a key named for a secret, masked; "•••" for text that is not a JSON
    /// object, which could be the secret itself.
    static func clientInfo(_ text: String) -> String {
        guard let value = try? JSONValue.parse(Data(text.utf8)), case .object = value else { return mask }
        return compactText(leaves(value, .dialog, secret: false))
    }

    /// The flag whose value holds an OAuth client's secret.
    static let oauthClientInfoFlag = "--static-oauth-client-info"

    /// Whether the value after `flag` is a secret: after any flag named for one when masked, and
    /// only after the OAuth client info in the dialog.
    static func hidesNext(_ flag: String, _ exposure: Exposure) -> Bool {
        if exposure == .dialog { return flag == oauthClientInfoFlag }
        return isFlag(flag) && cut(flag, at: "=") == nil && CredentialHeuristics.namesASecret(flag)
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

    /// Whether a key's values are secrets: `env` and `headers` wherever they sit, and any key named
    /// for a secret.
    static func namesASecret(_ key: String) -> Bool {
        secretKeys.contains(key) || CredentialHeuristics.namesASecret(key)
    }

    /// The config as structured output carries it, in its own shape: `env` and `headers` values
    /// become "set" or "needs value", and so does every string or number under a key named for a
    /// secret; an array is an argument list through `arguments(_:)`, and every other string goes
    /// through `argument(_:)`, a URL through `url(_:)`.
    public static func maskedConfig(_ config: JSONValue, reveal: Bool = false) -> JSONValue {
        guard !reveal else { return config }
        guard case .object(let object) = config else { return leaves(config, .masked, secret: false) }
        var out: [String: JSONValue] = [:]
        for (key, value) in object {
            if secretKeys.contains(key), case .object(let values) = value {
                out[key] = .object(values.mapValues { item in
                    if case .string(let text) = item { return .string(secretState(text)) }
                    return .string(setState)
                })
            } else {
                out[key] = leaves(value, .masked, secret: namesASecret(key))
            }
        }
        return .object(out)
    }

    /// `value` as `exposure` shows it: under a key named for a secret (`secret`) every string or
    /// number reads "set" or "needs value"; masked, an array is an argument list, its strings
    /// masked as `arguments(_:)` masks them and anything after a flag named for a secret too, and
    /// every other string goes through `argument(_:)`; in the dialog an array is an argument list as
    /// the dialog shows one, and the rest is as written.
    static func leaves(_ value: JSONValue, _ exposure: Exposure, secret: Bool) -> JSONValue {
        guard exposure != .revealed else { return value }
        switch value {
        case .string(let text):
            if secret { return .string(secretState(text)) }
            return exposure == .masked ? .string(argument(text)) : value
        case .int, .double:
            return secret ? .string(setState) : value
        case .array(let items):
            if secret { return .array(items.map { leaves($0, exposure, secret: true) }) }
            let shown = arguments(items.map { item -> String in
                if case .string(let text) = item { return text }
                return ""
            }, exposure: exposure)
            return .array(zip(items, shown).map { item, text in
                if case .string = item { return .string(text) }
                return leaves(item, exposure, secret: text == mask)
            })
        case .object(let object):
            var out: [String: JSONValue] = [:]
            for (key, item) in object { out[key] = leaves(item, exposure, secret: secret || namesASecret(key)) }
            return .object(out)
        case .null, .bool:
            return value
        }
    }

    // MARK: Lines

    /// One field of a config as a person reads it: its label, and each value as written (`raw`,
    /// which tells a changed secret from an unchanged one) and as shown.
    struct Field {
        let label: String
        var values: [(raw: String, shown: String)]
    }

    /// What a connector runs, one field per line, for `get_connector`'s text and the confirmation
    /// dialogs: a connector reached through mcp-remote by its URL and how it signs in; one reached
    /// by URL alone by its URL and headers; anything else by its command, arguments and environment.
    /// Every top-level key those do not show in full follows on a line of its own, so the add
    /// dialog shows everything that would run. Fields that share a label share its line.
    public static func codeLines(_ config: JSONValue, exposure: Exposure = .masked) -> [String] {
        let all = fields(config, exposure)
        var labels: [String] = []
        for field in all where !labels.contains(field.label) { labels.append(field.label) }
        return ControlText.labelled(labels.flatMap { label in
            all.filter { $0.label == label }.flatMap(\.values).enumerated().map { index, value in
                (index == 0 ? label : "", value.shown)
            }
        })
    }

    /// The fields an edit changes, for the change confirmation: each changed field's label, then
    /// "− " before each value it loses and "+ " before each it gains. A secret that changed shows
    /// as changed although both sides read "••• (set)". Every difference between the two configs
    /// shows, as every key shows on the code lines.
    public static func changeLines(from before: JSONValue, to after: JSONValue, exposure: Exposure = .masked) -> [String] {
        let old = fields(before, exposure)
        let new = fields(after, exposure)
        var labels: [String] = []
        for field in new + old where !labels.contains(field.label) { labels.append(field.label) }
        var lines: [String] = []
        for label in labels {
            let was = old.filter { $0.label == label }.flatMap(\.values)
            let now = new.filter { $0.label == label }.flatMap(\.values)
            let lost = was.filter { item in !now.contains { same($0, item) } }
            let gained = now.filter { item in !was.contains { same($0, item) } }
            guard !lost.isEmpty || !gained.isEmpty else { continue }
            lines.append(label)
            lines += lost.map { removedPrefix + $0.shown }
            lines += gained.map { addedPrefix + $0.shown }
        }
        return lines
    }

    // Platform idiom: values are compared scalar by scalar, as the Windows mirror compares them
    // ordinally; Swift's == holds canonically equivalent text equal, which would hide a change.
    static func same(_ one: (raw: String, shown: String), _ other: (raw: String, shown: String)) -> Bool {
        same(one.raw, other.raw) && same(one.shown, other.shown)
    }

    static func same(_ one: String, _ other: String) -> Bool {
        one.unicodeScalars.elementsEqual(other.unicodeScalars)
    }

    /// The fields the code lines and change lines show: the shape's own, then a field for each
    /// top-level key those do not show in full.
    static func fields(_ config: JSONValue, _ exposure: Exposure) -> [Field] {
        guard case .object(let object) = config else {
            return [Field(label: otherLabel, values: [(raw(.array([config])), valueText("", config, exposure))])]
        }
        let (fields, shown) = view(config, exposure)
        return fields + object.keys.filter { !shown.contains($0) }
            .sorted { $0.ordinallyPrecedes($1) }
            .map { key in
                // Raw with its key, so it never matches a field's raw under the same label (a
                // `url` key beside mcp-remote's URL).
                Field(label: key, values: [(raw(.array([.string(key), object[key] ?? .null])), valueText(key, object[key] ?? .null, exposure))])
            }
    }

    /// A top-level key's value on a line, after `leaves`: a string as itself, anything else as one
    /// line of JSON; a string under a key named for a secret as `secretText` shows it.
    static func valueText(_ key: String, _ value: JSONValue, _ exposure: Exposure) -> String {
        if case .string(let text) = value {
            if namesASecret(key) { return secretText(text, reveal: exposure == .revealed) }
            return exposure == .masked ? argument(text) : text
        }
        return compactText(leaves(value, exposure, secret: namesASecret(key)))
    }

    /// A value as written, for telling one from another: every part of it, nothing shown.
    static func raw(_ value: JSONValue) -> String {
        compactText(value)
    }

    // Platform idiom: JSONEncoder with sorted keys and unescaped slashes, which the Windows mirror
    // writes byte for byte as AppleJsonFormat.EditorText without its line breaks.
    /// One line of JSON.
    static func compactText(_ value: JSONValue) -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        return (try? encoder.encode(value)).map { String(decoding: $0, as: UTF8.self) } ?? ""
    }

    /// The fields of `config` as `exposure` shows them, and the top-level keys they show in full.
    static func view(_ config: JSONValue, _ exposure: Exposure) -> (fields: [Field], shown: Set<String>) {
        guard case .object(let object) = config else { return ([], []) }
        // Platform idiom: RemotePattern.decode on Windows also reads `cmd /c npx …`, so a Windows
        // connector shows the same lines; the Mac has no such launcher.
        if let remote = RemotePattern.decode(config) { return remoteView(object, remote, exposure) }
        if object["command"] == nil, case .string(let address)? = object["url"] { return urlView(object, address, exposure) }
        return localView(config, object, exposure)
    }

    /// A connector reached through mcp-remote: its bridge, URL and sign-in, every argument those
    /// do not show, and its environment but the variable a header reads. The sign-in shows only
    /// the flags `RemotePattern.encode` writes for it; one written any other way (spacing, a second
    /// copy, a field the decoder drops) shows among the arguments. The bridge's value as written
    /// holds the launcher, so dropping `-y` shows as a change.
    static func remoteView(_ object: [String: JSONValue], _ remote: RemoteConfig,
                           _ exposure: Exposure) -> (fields: [Field], shown: Set<String>) {
        let args = strings(object["args"])
        let bridge = args.firstIndex { same($0, remote.package) } ?? 0
        let launcher = [object["command"] ?? .null] + args[...bridge].map(JSONValue.string)
        var fields = [Field(label: typeLabel, values: [(raw(.array(launcher)), remoteBridgeType(remote.package))]),
                      Field(label: urlLabel, values: [(raw(.string(remote.url)), url(remote.url, reveal: exposure != .masked))])]
        let reveal = exposure == .revealed
        switch remote.auth {
        case .automatic:
            break
        case .bearer(let token):
            fields.append(Field(label: headersLabel,
                                values: [(raw(.array([.string(token)])), assignment("Authorization", secretText(token, reveal: reveal)))]))
        case .header(let name, let value):
            fields.append(Field(label: headersLabel,
                                values: [(raw(.array([.string(name), .string(value)])), assignment(name, secretText(value, reveal: reveal)))]))
        case .oauthClient(let clientID, let clientSecret, let scopes):
            fields.append(Field(label: oauthLabel,
                                values: [(raw(.array([.string(clientID), .string(clientSecret)])),
                                          oauthClientText(clientID, secretText(clientSecret, reveal: reveal)))]))
            if !scopes.isEmpty { fields.append(Field(label: scopesLabel, values: [(raw(.string(scopes)), scopes)])) }
        }
        let signIn = signInArguments(remote.auth)
        let rest = unmodelled(Array(args.dropFirst(bridge + 2)), signIn.pairs)
        let kept = rest.compactMap { $0 }
        if !kept.isEmpty {
            fields.append(Field(label: argsLabel, values: [(raw(.array(rest.map { $0.map(JSONValue.string) ?? .null })),
                                                            arguments(kept, exposure: exposure).joined(separator: " "))]))
        }
        var shown: Set<String> = ["command", "args"]
        if case .object(let env)? = object["env"], !env.isEmpty {
            let passthrough = env.filter { !signIn.variables.contains($0.key) }
            if !passthrough.isEmpty { fields.append(secretField(envLabel, passthrough, exposure)) }
            shown.insert("env")
        }
        return (fields, shown)
    }

    /// The sign-in flags and values `RemotePattern.encode` writes for `auth`, in pairs, and the
    /// variables it sets for them.
    static func signInArguments(_ auth: RemoteAuth) -> (pairs: [[String]], variables: Set<String>) {
        // Platform idiom: encoded with the plain npx launcher, the Mac's only one; the Windows mirror
        // names it, so its pairs start where these do.
        guard case .object(let object) = RemotePattern.encode(RemoteConfig(url: "", auth: auth)) else { return ([], []) }
        let args = strings(object["args"])
        var variables: Set<String> = []
        if case .object(let env)? = object["env"] { variables = Set(env.keys) }
        // Past "-y", the bridge and the URL, encode writes nothing but sign-in pairs.
        return (stride(from: 3, to: args.count - 1, by: 2).map { [args[$0], args[$0 + 1]] }, variables)
    }

    /// The arguments after the URL with each of `signIn`'s pairs that they hold, once, replaced by
    /// nil, where the sign-in fields show it, so where a pair stands among the other arguments
    /// shows but not the order of two pairs, which mcp-remote reads by name. A pair is a flag and
    /// its value as the decoder pairs them: `--header`, `--static-oauth-client-info` or
    /// `--static-oauth-client-metadata` before another argument.
    static func unmodelled(_ args: [String], _ signIn: [[String]]) -> [String?] {
        var expected = signIn
        var kept: [String?] = []
        var index = 0
        while index < args.count {
            if signInFlags.contains(args[index]), index + 1 < args.count {
                let pair = [args[index], args[index + 1]]
                if let match = expected.firstIndex(where: { $0.count == 2 && same($0[0], pair[0]) && same($0[1], pair[1]) }) {
                    expected.remove(at: match)
                    kept.append(nil)
                } else {
                    kept += pair.map { $0 }
                }
                index += 2
            } else {
                kept.append(args[index])
                index += 1
            }
        }
        return kept
    }

    static let signInFlags = ["--header", oauthClientInfoFlag, "--static-oauth-client-metadata"]

    /// A connector reached by URL alone: its URL and headers.
    static func urlView(_ object: [String: JSONValue], _ address: String,
                        _ exposure: Exposure) -> (fields: [Field], shown: Set<String>) {
        var fields = [Field(label: typeLabel, values: [(raw(.null), CollectionsModel.remoteType)]),
                      Field(label: urlLabel, values: [(raw(.string(address)), url(address, reveal: exposure != .masked))])]
        var shown: Set<String> = ["url"]
        if case .object(let headers)? = object["headers"], !headers.isEmpty {
            fields.append(secretField(headersLabel, headers, exposure))
            shown.insert("headers")
        }
        return (fields, shown)
    }

    /// Anything else: its command, arguments and environment.
    static func localView(_ config: JSONValue, _ object: [String: JSONValue],
                          _ exposure: Exposure) -> (fields: [Field], shown: Set<String>) {
        let model = FormMapper.analyze(config).model
        var fields = [Field(label: commandLabel, values: [(raw(object["command"] ?? .null),
                                                           exposure == .masked ? argument(model.command) : model.command)])]
        var shown: Set<String> = []
        if case .string? = object["command"] { shown.insert("command") }
        if !model.args.isEmpty {
            fields.append(Field(label: argsLabel, values: [(raw(.array(model.args.map(JSONValue.string))),
                                                            arguments(model.args, exposure: exposure).joined(separator: " "))]))
        }
        if case .array(let items)? = object["args"], !items.isEmpty, items.count == model.args.count { shown.insert("args") }
        if case .object(let env)? = object["env"], !env.isEmpty {
            fields.append(secretField(envLabel, env, exposure))
            shown.insert("env")
        }
        return (fields, shown)
    }

    /// Environment variables or headers by name, each value as `secretText` shows it; one that is
    /// not a string is set, and shows as JSON only when revealed.
    static func secretField(_ label: String, _ values: [String: JSONValue], _ exposure: Exposure) -> Field {
        Field(label: label, values: values.keys.sorted { $0.ordinallyPrecedes($1) }.map { name in
            let value = values[name] ?? .null
            var shown = exposure == .revealed ? compactText(value) : setText
            if case .string(let text) = value { shown = secretText(text, reveal: exposure == .revealed) }
            return (raw(.array([.string(name), value])), assignment(name, shown))
        })
    }

    /// The strings of an array value; nothing else.
    static func strings(_ value: JSONValue?) -> [String] {
        guard case .array(let items)? = value else { return [] }
        return items.compactMap { item in
            if case .string(let text) = item { return text }
            return nil
        }
    }
}

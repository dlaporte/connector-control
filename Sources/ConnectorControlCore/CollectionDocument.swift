import Foundation

public enum CollectionPlatform: String, Equatable, Sendable {
    case mac, windows
    /// The platform this build renders launchers for. Stated here rather than detected at run
    /// time so the tests on either side exercise exactly one, known rendering.
    public static let current: CollectionPlatform = .mac
}

public enum CollectionDocumentError: Error, Equatable {
    case newerFormat(Int)
    case malformed(String)
}

/// What the author ticked in the Publish sheet: which env values travel as values rather than
/// as stripped hints, which arguments become markers, and the hint text for each.
public struct PublishIntent: Equatable, Sendable {
    public struct PathMark: Equatable, Sendable {
        public var name: String
        public var hint: String?
        public init(name: String, hint: String?) { self.name = name; self.hint = hint }
    }
    public var shareValues: [String: Set<String>]
    public var pathMarks: [String: [JSONPointer: PathMark]]
    public var hints: [String: [String: String]]
    public init(shareValues: [String: Set<String>], pathMarks: [String: [JSONPointer: PathMark]], hints: [String: [String: String]]) {
        self.shareValues = shareValues
        self.pathMarks = pathMarks
        self.hints = hints
    }
    public static let none = PublishIntent(shareValues: [:], pathMarks: [:], hints: [:])
}

public struct RenderedNeed: Equatable, Sendable {
    public var hint: String?
    public var pointer: JSONPointer
    public init(hint: String?, pointer: JSONPointer) { self.hint = hint; self.pointer = pointer }
}

public struct RenderedConnector: Equatable, Sendable {
    public var config: JSONValue
    public var needs: [String: RenderedNeed]
    /// The platform the author's machine wrote a local launcher on; nil for a remote connector,
    /// whose launcher each importer builds for itself.
    public var authoredOn: CollectionPlatform?
    public init(config: JSONValue, needs: [String: RenderedNeed], authoredOn: CollectionPlatform?) {
        self.config = config
        self.needs = needs
        self.authoredOn = authoredOn
    }
}

public struct RenderedCollection: Equatable, Sendable {
    public var connectors: [String: RenderedConnector]
    /// Connector name → why this platform cannot carry it; always empty on the Mac.
    public var excluded: [String: String]
    public init(connectors: [String: RenderedConnector], excluded: [String: String]) {
        self.connectors = connectors
        self.excluded = excluded
    }
}

/// The document a collection travels as. Remote connectors are stored in the neutral form the
/// editor already uses, so each importer renders its own launcher; secrets and marked paths are
/// placeholders; enabled flags never travel.
///
/// Mirror: windows/src/ConnectorControl.Core/CollectionDocument.cs
public struct CollectionDocument: Equatable, Sendable {
    public static let formatVersion = 1
    public static let fileExtension = "json"
    public static let tokenNeed = "token"
    public static let headerValueNeed = "header_value"
    public static let clientSecretNeed = "client_secret"

    public var name: String
    public var author: String?
    public var origin: String?
    /// ISO 8601 UTC, e.g. "2026-09-21T14:02:11Z". Stored as text: the document never does date
    /// arithmetic, and a string cannot drift between two platforms' formatters.
    public var exported: String
    public var connectors: [String: Connector]

    public init(name: String, author: String?, origin: String?, exported: String, connectors: [String: Connector]) {
        self.name = name
        self.author = author
        self.origin = origin
        self.exported = exported
        self.connectors = connectors
    }

    public struct Connector: Equatable, Sendable {
        public var launcher: Launcher
        public var env: [String: EnvValue]
        /// Placeholder name → hint (nil: no hint).
        public var needs: [String: String?]
        public var additional: [String: JSONValue]

        public init(launcher: Launcher, env: [String: EnvValue] = [:],
                    needs: [String: String?] = [:], additional: [String: JSONValue] = [:]) {
            self.launcher = launcher
            self.env = env
            self.needs = needs
            self.additional = additional
        }
    }

    public enum Launcher: Equatable, Sendable { case remote(Remote), local(Local) }

    public struct Remote: Equatable, Sendable {
        public var url: String
        public var auth: Auth
        public var package: String
        public var extraArgs: [String]
        public init(url: String, auth: Auth, package: String, extraArgs: [String]) {
            self.url = url
            self.auth = auth
            self.package = package
            self.extraArgs = extraArgs
        }
    }

    public enum Auth: Equatable, Sendable {
        /// C# has no case value of this name to write — a record type's name isn't an
        /// expression there — so it exposes the same case through a static `Auto` instead.
        case automatic
        case bearer
        case header(name: String)
        case oauthClient(clientId: String, scopes: String)
    }

    public struct Local: Equatable, Sendable {
        public var command: String
        public var args: [String]
        public var platform: CollectionPlatform
        public init(command: String, args: [String], platform: CollectionPlatform) {
            self.command = command
            self.args = args
            self.platform = platform
        }
    }

    public enum EnvValue: Equatable, Sendable { case hint(String?), value(String) }

    public var fileName: String { Slug.make(name) + "." + Self.fileExtension }

    // MARK: Encode

    public func encode() -> JSONValue {
        var root: [String: JSONValue] = [
            "connectorControlCollection": .int(Self.formatVersion),
            "name": .string(name),
            "exported": .string(exported),
            "connectors": .object(connectors.mapValues { $0.encode() }),
        ]
        if let author { root["author"] = .string(author) }
        if let origin { root["origin"] = .string(origin) }
        return .object(root)
    }

    public func serialized() throws -> Data { try encode().serialized() }

    // MARK: Decode

    public static func decode(_ data: Data) throws -> CollectionDocument {
        let json: JSONValue
        do { json = try JSONValue.parse(data) } catch { throw CollectionDocumentError.malformed("not JSON: \(error.localizedDescription)") }
        return try decode(json)
    }

    public static func decode(_ json: JSONValue) throws -> CollectionDocument {
        guard case .object(let root) = json else { throw CollectionDocumentError.malformed("top level is not a JSON object") }
        guard case .int(let version)? = root["connectorControlCollection"] else {
            throw CollectionDocumentError.malformed("connectorControlCollection is missing")
        }
        if version > formatVersion { throw CollectionDocumentError.newerFormat(version) }
        let name = try requiredString(root["name"], "name")
        let author = try optionalString(root["author"], "author")
        let origin = try optionalString(root["origin"], "origin")
        let exported = try requiredString(root["exported"], "exported")
        guard let rawConnectors = root["connectors"] else { throw CollectionDocumentError.malformed("connectors is missing") }
        guard case .object(let connectorObjects) = rawConnectors else {
            throw CollectionDocumentError.malformed("connectors is not a JSON object")
        }
        var connectors: [String: Connector] = [:]
        for (key, value) in connectorObjects {
            connectors[key] = try Connector.decode(value, connector: key)
        }
        return CollectionDocument(name: name, author: author, origin: origin, exported: exported, connectors: connectors)
    }

    // MARK: Render (this platform)

    public func render() -> RenderedCollection {
        var out: [String: RenderedConnector] = [:]
        for (name, connector) in connectors {
            let env = connector.env.reduce(into: [String: String]()) { acc, pair in
                switch pair.value {
                case .hint: acc[pair.key] = Placeholder.marker(pair.key)
                case .value(let v): acc[pair.key] = v
                }
            }
            let config: JSONValue
            var authoredOn: CollectionPlatform?
            switch connector.launcher {
            case .remote(let r):
                let auth: RemoteAuth
                switch r.auth {
                case .automatic: auth = .automatic
                case .bearer: auth = .bearer(token: Placeholder.marker(Self.tokenNeed))
                case .header(let n): auth = .header(name: n, value: Placeholder.marker(Self.headerValueNeed))
                case .oauthClient(let id, let scopes):
                    auth = .oauthClient(clientID: id, clientSecret: Placeholder.marker(Self.clientSecretNeed), scopes: scopes)
                }
                let encoded = RemotePattern.encode(RemoteConfig(url: r.url, auth: auth, extraArgs: r.extraArgs,
                                                                 passthroughEnv: env, package: r.package))
                config = Self.merging(additional: connector.additional, into: encoded)
            case .local(let l):
                config = FormMapper.serialize(FormModel(command: l.command, args: l.args, env: env, additional: connector.additional))
                authoredOn = l.platform
            }
            var needs: [String: RenderedNeed] = [:]
            for (pointer, names) in Placeholder.markers(in: config) {
                for n in names {
                    let hint = connector.needs[n] ?? connector.env[n].flatMap { value in
                        if case .hint(let h) = value { return h } else { return nil }
                    }
                    needs[n] = RenderedNeed(hint: hint, pointer: pointer)
                }
            }
            out[name] = RenderedConnector(config: config, needs: needs, authoredOn: authoredOn)
        }
        // The Windows build excludes connectors the cmd /c launcher cannot carry safely; a Mac
        // never writes that launcher, so nothing is excluded here.
        return RenderedCollection(connectors: out, excluded: [:])
    }

    /// A remote connector's `additional` fields (whatever the form the config came from cannot
    /// represent) merged into the freshly encoded launcher config; the encoded keys — always
    /// `command`/`args`, sometimes `env` — win on a collision, since they are what makes the
    /// connector run.
    private static func merging(additional: [String: JSONValue], into config: JSONValue) -> JSONValue {
        guard !additional.isEmpty, case .object(let encoded) = config else { return config }
        return .object(additional.merging(encoded) { _, encodedValue in encodedValue })
    }

    // MARK: Export

    public static func export(name: String, author: String?, origin: String?, exported: String,
                              connectors: [String: JSONValue], intent: PublishIntent) -> CollectionDocument {
        var out: [String: Connector] = [:]
        for (connectorName, config) in connectors {
            let shared = intent.shareValues[connectorName] ?? []
            let hints = intent.hints[connectorName] ?? [:]
            func envValue(_ key: String, _ value: String) -> EnvValue { shared.contains(key) ? .value(value) : .hint(hints[key]) }
            var needs: [String: String?] = [:]
            if let remote = RemotePattern.decode(config) {
                let auth: Auth
                switch remote.auth {
                case .automatic:
                    auth = .automatic
                case .bearer:
                    auth = .bearer
                    needs.updateValue(hints[tokenNeed], forKey: tokenNeed)
                case .header(let n, _):
                    auth = .header(name: n)
                    needs.updateValue(hints[headerValueNeed], forKey: headerValueNeed)
                case .oauthClient(let id, _, let scopes):
                    auth = .oauthClient(clientId: id, scopes: scopes)
                    needs.updateValue(hints[clientSecretNeed], forKey: clientSecretNeed)
                }
                let env = remote.passthroughEnv.reduce(into: [String: EnvValue]()) { $0[$1.key] = envValue($1.key, $1.value) }
                // Whatever the form has no widget for — a key `RemotePattern.decode` doesn't
                // read — travels too, the same way a local connector's does.
                let additional = FormMapper.analyze(config).model.additional
                out[connectorName] = Connector(launcher: .remote(Remote(url: remote.url, auth: auth, package: remote.package,
                                                                        extraArgs: remote.extraArgs)),
                                               env: env, needs: needs, additional: additional)
            } else {
                let model = FormMapper.analyze(config).model
                var args = model.args
                for (pointer, mark) in intent.pathMarks[connectorName] ?? [:] {
                    guard pointer.segments.count == 2, pointer.segments[0] == "args",
                          let i = Int(pointer.segments[1]), args.indices.contains(i) else { continue }
                    args[i] = Placeholder.marker(mark.name)
                    needs.updateValue(mark.hint, forKey: mark.name)
                }
                // A marker the author already typed, or one an imported copy still carries, is a
                // need too — otherwise re-exporting a collection would drop what it asks for.
                for n in (args + [model.command]).flatMap(Placeholder.names(in:)) where needs.index(forKey: n) == nil {
                    needs.updateValue(hints[n], forKey: n)
                }
                let env = model.env.reduce(into: [String: EnvValue]()) { $0[$1.key] = envValue($1.key, $1.value) }
                out[connectorName] = Connector(launcher: .local(Local(command: model.command, args: args, platform: .current)),
                                               env: env, needs: needs, additional: model.additional)
            }
        }
        return CollectionDocument(name: name, author: author, origin: origin, exported: exported, connectors: out)
    }

    /// "args[N] looks like a credential" / "env.NAME looks like a credential" lines for the
    /// publish preview; never an edit. Each arg is tested whole and, for a literal "key: value"
    /// pair such as a `--header` flag's argument, on the text after the colon too, since the
    /// heuristic's own space check would otherwise hide a credential sitting right after one.
    /// Env is only tested for names in `sharedEnv` — the ones the author ticked to travel as a
    /// value rather than a hint — since a hint-only value never leaves this machine.
    public static func credentialWarnings(_ config: JSONValue, sharedEnv: Set<String>) -> [String] {
        guard case .object(let object) = config else { return [] }
        var warnings: [String] = []
        if case .array(let args)? = object["args"] {
            for (index, value) in args.enumerated() {
                guard case .string(let s) = value else { continue }
                let afterColon = s.range(of: ": ").map { String(s[$0.upperBound...]) }
                if CredentialHeuristics.looksLikeCredential(s) || (afterColon.map(CredentialHeuristics.looksLikeCredential) ?? false) {
                    warnings.append("args[\(index)] looks like a credential")
                }
            }
        }
        if case .object(let env)? = object["env"] {
            for name in sharedEnv.sorted() {
                guard case .string(let value)? = env[name], CredentialHeuristics.looksLikeCredential(value) else { continue }
                warnings.append("env.\(name) looks like a credential")
            }
        }
        return warnings
    }

    // MARK: Decoding helpers
    // Every failure names the key it read, so a hand-edited document says what is wrong with it.

    static func requiredString(_ value: JSONValue?, _ what: String) throws -> String {
        guard let value else { throw CollectionDocumentError.malformed("\(what) is missing") }
        guard case .string(let s) = value else { throw CollectionDocumentError.malformed("\(what) is not a string") }
        return s
    }

    static func optionalString(_ value: JSONValue?, _ what: String) throws -> String? {
        guard let value, value != .null else { return nil }
        guard case .string(let s) = value else { throw CollectionDocumentError.malformed("\(what) is not a string") }
        return s
    }

    static func stringArray(_ value: JSONValue?, _ what: String) throws -> [String] {
        guard let value else { return [] }
        guard case .array(let items) = value else { throw CollectionDocumentError.malformed("\(what) is not an array") }
        return try items.enumerated().map { index, item in
            guard case .string(let s) = item else { throw CollectionDocumentError.malformed("\(what)[\(index)] is not a string") }
            return s
        }
    }

    static func objectValue(_ value: JSONValue?, _ what: String) throws -> [String: JSONValue] {
        guard let value else { return [:] }
        guard case .object(let object) = value else { throw CollectionDocumentError.malformed("\(what) is not a JSON object") }
        return object
    }
}

// MARK: - Nested encode / decode

extension CollectionDocument.Connector {
    func encode() -> JSONValue {
        var object: [String: JSONValue] = [:]
        switch launcher {
        case .remote(let r): object["remote"] = r.encode()
        case .local(let l): object["local"] = l.encode()
        }
        object["env"] = .object(env.mapValues { $0.encode() })
        object["needs"] = .object(needs.mapValues { .object(["hint": $0.map(JSONValue.string) ?? .null]) })
        object["additional"] = .object(additional)
        return .object(object)
    }

    static func decode(_ json: JSONValue, connector: String) throws -> CollectionDocument.Connector {
        let what = "connector \"\(connector)\""
        guard case .object(let object) = json else { throw CollectionDocumentError.malformed("\(what) is not a JSON object") }
        let launcher: CollectionDocument.Launcher
        switch (object["remote"], object["local"]) {
        case (.some(let remote), .none):
            launcher = .remote(try CollectionDocument.Remote.decode(remote, what: "\(what) remote"))
        case (.none, .some(let local)):
            launcher = .local(try CollectionDocument.Local.decode(local, what: "\(what) local"))
        default:
            throw CollectionDocumentError.malformed("\(what) needs exactly one of remote or local")
        }
        var env: [String: CollectionDocument.EnvValue] = [:]
        for (key, value) in try CollectionDocument.objectValue(object["env"], "\(what) env") {
            env[key] = try CollectionDocument.EnvValue.decode(value, what: "\(what) env \"\(key)\"")
        }
        var needs: [String: String?] = [:]
        for (key, value) in try CollectionDocument.objectValue(object["needs"], "\(what) needs") {
            let entryWhat = "\(what) need \"\(key)\""
            let entry = try CollectionDocument.objectValue(value, entryWhat)
            needs.updateValue(try CollectionDocument.optionalString(entry["hint"], "\(entryWhat) hint"), forKey: key)
        }
        let additional = try CollectionDocument.objectValue(object["additional"], "\(what) additional")
        return CollectionDocument.Connector(launcher: launcher, env: env, needs: needs, additional: additional)
    }
}

extension CollectionDocument.Remote {
    func encode() -> JSONValue {
        .object([
            "url": .string(url),
            "auth": auth.encode(),
            "package": .string(package),
            "extraArgs": .array(extraArgs.map(JSONValue.string)),
        ])
    }

    static func decode(_ json: JSONValue, what: String) throws -> CollectionDocument.Remote {
        guard case .object(let object) = json else { throw CollectionDocumentError.malformed("\(what) is not a JSON object") }
        guard let rawAuth = object["auth"] else { throw CollectionDocumentError.malformed("\(what) auth is missing") }
        return CollectionDocument.Remote(
            url: try CollectionDocument.requiredString(object["url"], "\(what) url"),
            auth: try CollectionDocument.Auth.decode(rawAuth, what: "\(what) auth"),
            package: try CollectionDocument.requiredString(object["package"], "\(what) package"),
            extraArgs: try CollectionDocument.stringArray(object["extraArgs"], "\(what) extraArgs"))
    }
}

extension CollectionDocument.Auth {
    func encode() -> JSONValue {
        switch self {
        case .automatic: return .object(["kind": .string("automatic")])
        case .bearer: return .object(["kind": .string("bearer")])
        case .header(let name): return .object(["kind": .string("header"), "name": .string(name)])
        case .oauthClient(let clientId, let scopes):
            return .object(["kind": .string("oauthClient"), "clientId": .string(clientId), "scopes": .string(scopes)])
        }
    }

    static func decode(_ json: JSONValue, what: String) throws -> CollectionDocument.Auth {
        guard case .object(let object) = json else { throw CollectionDocumentError.malformed("\(what) is not a JSON object") }
        let kind = try CollectionDocument.requiredString(object["kind"], "\(what) kind")
        switch kind {
        case "automatic": return .automatic
        case "bearer": return .bearer
        case "header": return .header(name: try CollectionDocument.requiredString(object["name"], "\(what) name"))
        case "oauthClient":
            return .oauthClient(clientId: try CollectionDocument.requiredString(object["clientId"], "\(what) clientId"),
                                scopes: try CollectionDocument.optionalString(object["scopes"], "\(what) scopes") ?? "")
        default:
            throw CollectionDocumentError.malformed("\(what) kind \"\(kind)\" is not automatic, bearer, header or oauthClient")
        }
    }
}

extension CollectionDocument.Local {
    func encode() -> JSONValue {
        .object([
            "command": .string(command),
            "args": .array(args.map(JSONValue.string)),
            "platform": .string(platform.rawValue),
        ])
    }

    static func decode(_ json: JSONValue, what: String) throws -> CollectionDocument.Local {
        guard case .object(let object) = json else { throw CollectionDocumentError.malformed("\(what) is not a JSON object") }
        let rawPlatform = try CollectionDocument.requiredString(object["platform"], "\(what) platform")
        guard let platform = CollectionPlatform(rawValue: rawPlatform) else {
            throw CollectionDocumentError.malformed("\(what) platform \"\(rawPlatform)\" is not mac or windows")
        }
        return CollectionDocument.Local(
            command: try CollectionDocument.requiredString(object["command"], "\(what) command"),
            args: try CollectionDocument.stringArray(object["args"], "\(what) args"),
            platform: platform)
    }
}

extension CollectionDocument.EnvValue {
    func encode() -> JSONValue {
        switch self {
        case .hint(let hint): return .object(["hint": hint.map(JSONValue.string) ?? .null])
        case .value(let value): return .object(["value": .string(value)])
        }
    }

    static func decode(_ json: JSONValue, what: String) throws -> CollectionDocument.EnvValue {
        guard case .object(let object) = json else { throw CollectionDocumentError.malformed("\(what) is not a JSON object") }
        if let value = object["value"] {
            return .value(try CollectionDocument.requiredString(value, "\(what) value"))
        }
        guard object.index(forKey: "hint") != nil else {
            throw CollectionDocumentError.malformed("\(what) has neither hint nor value")
        }
        return .hint(try CollectionDocument.optionalString(object["hint"], "\(what) hint"))
    }
}

import Foundation

public enum CollectionKind: String, Equatable, Sendable {
    case local, synced
}

public enum CollectionsFileError: Error, Equatable {
    case malformed(String)
}

/// The sidecar written beside the master list: for each collection that has something to say,
/// whether it is synced or published, where its document is relative to the store, and the
/// placeholders it carries. It travels with the master list, so it holds only what is true on
/// every machine — the per-machine bindings live in `CollectionsLocalCache`.
///
/// Mirror: windows/src/ConnectorControl.Core/CollectionsFile.cs
public struct CollectionsFile: Equatable, Sendable {
    public static let fileName = "collections.json"
    public static let formatVersion = 1

    /// Only collections with something to say: a local collection that was neither published
    /// nor imported from has no entry at all.
    public var collections: [String: Entry]

    public init(collections: [String: Entry]) { self.collections = collections }

    public struct Entry: Equatable, Sendable {
        public var kind: CollectionKind
        /// Synced: the document's file name.
        public var fileName: String?
        /// Synced: the document's path relative to the store dir, when it lies inside that tree.
        public var relativeToStore: String?
        /// Synced: the document's origin.
        public var origin: String?
        /// Synced: connector → placeholder name → what the last Apply asked for.
        public var needs: [String: [String: Need]]
        /// Local: what publishing this collection fixed.
        public var publish: PublishRecord?
        /// Local: connector → where an imported copy came from.
        public var provenance: [String: Provenance]

        public init(kind: CollectionKind, fileName: String? = nil, relativeToStore: String? = nil,
                    origin: String? = nil, needs: [String: [String: Need]] = [:],
                    publish: PublishRecord? = nil, provenance: [String: Provenance] = [:]) {
            self.kind = kind
            self.fileName = fileName
            self.relativeToStore = relativeToStore
            self.origin = origin
            self.needs = needs
            self.publish = publish
            self.provenance = provenance
        }

        /// An ordinary local collection: the entry the file leaves out entirely.
        public static let local = Entry(kind: .local)
    }

    public struct Need: Equatable, Sendable {
        public var hint: String?
        /// Where the marker sits inside the connector's config, so a filled value follows the
        /// marker when the author reorders arguments.
        public var pointer: JSONPointer
        public init(hint: String?, pointer: JSONPointer) { self.hint = hint; self.pointer = pointer }
    }

    public struct PublishRecord: Equatable, Sendable {
        /// Fixed when publishing starts and never re-derived, so a rename cannot orphan the
        /// document importers already hold.
        public var slug: String
        public var origin: String
        public var intent: PublishIntent
        public init(slug: String, origin: String, intent: PublishIntent) {
            self.slug = slug
            self.origin = origin
            self.intent = intent
        }
    }

    public struct Provenance: Equatable, Sendable {
        public var from: String
        public var author: String?
        /// The local calendar date of the import, as text; the file never does date arithmetic.
        public var date: String
        public init(from: String, author: String?, date: String) {
            self.from = from
            self.author = author
            self.date = date
        }
    }

    /// A collection with no entry is local — a profile the master list has and the sidecar has
    /// never had anything to add about.
    public func kind(of name: String) -> CollectionKind { collections[name]?.kind ?? .local }

    // MARK: Encode

    public func encode() -> JSONValue {
        var out: [String: JSONValue] = [:]
        for (name, entry) in collections where entry != .local {
            out[name] = entry.encode()
        }
        return .object(["version": .int(Self.formatVersion), "collections": .object(out)])
    }

    // MARK: Decode

    public static func decode(_ json: JSONValue) throws -> CollectionsFile {
        guard case .object(let root) = json else { throw CollectionsFileError.malformed("top level is not a JSON object") }
        guard case .int(let version)? = root["version"] else { throw CollectionsFileError.malformed("version is missing") }
        guard version == formatVersion else {
            throw CollectionsFileError.malformed("version \(version) is not \(formatVersion)")
        }
        var collections: [String: Entry] = [:]
        for (name, value) in try objectValue(root["collections"], "collections") {
            collections[name] = try Entry.decode(value, name: name)
        }
        return CollectionsFile(collections: collections)
    }

    // MARK: Disk

    /// A missing or unreadable sidecar loads as empty, and the file on disk is left exactly as
    /// it was: everything here is derived from the master list and the documents beside it, so
    /// losing it costs hints and origins the app can rebuild — unlike a corrupt mcps.json,
    /// there is nothing to move aside and preserve.
    public static func load(from url: URL) -> CollectionsFile {
        guard let data = try? Data(contentsOf: url),
              let json = try? JSONValue.parse(data),
              let file = try? decode(json) else { return CollectionsFile(collections: [:]) }
        return file
    }

    public func save(to url: URL, staging: URL?) throws {
        try AtomicFile.write(encode().serialized(), to: url, staging: staging)
    }

    /// Drops entries whose name is not a collection in the store: the master list decides which
    /// collections exist, and the sidecar only ever annotates them.
    public func reconciled(with store: MasterStore) -> CollectionsFile {
        CollectionsFile(collections: collections.filter { store.collections.keys.contains($0.key) })
    }

    // MARK: Decoding helpers
    // Every failure names the key it read, so a hand-edited sidecar says what is wrong with it.

    static func requiredString(_ value: JSONValue?, _ what: String) throws -> String {
        guard let value else { throw CollectionsFileError.malformed("\(what) is missing") }
        guard case .string(let s) = value else { throw CollectionsFileError.malformed("\(what) is not a string") }
        return s
    }

    static func optionalString(_ value: JSONValue?, _ what: String) throws -> String? {
        guard let value, value != .null else { return nil }
        guard case .string(let s) = value else { throw CollectionsFileError.malformed("\(what) is not a string") }
        return s
    }

    static func objectValue(_ value: JSONValue?, _ what: String) throws -> [String: JSONValue] {
        guard let value else { return [:] }
        guard case .object(let object) = value else { throw CollectionsFileError.malformed("\(what) is not a JSON object") }
        return object
    }

    static func stringSet(_ value: JSONValue?, _ what: String) throws -> Set<String> {
        guard let value else { return [] }
        guard case .array(let items) = value else { throw CollectionsFileError.malformed("\(what) is not an array") }
        return Set(try items.enumerated().map { index, item in
            guard case .string(let s) = item else { throw CollectionsFileError.malformed("\(what)[\(index)] is not a string") }
            return s
        })
    }

    static func pointer(_ text: String, _ what: String) throws -> JSONPointer {
        guard let pointer = JSONPointer(string: text) else {
            throw CollectionsFileError.malformed("\(what) \"\(text)\" is not a JSON pointer")
        }
        return pointer
    }
}

// MARK: - Nested encode / decode

extension CollectionsFile.Entry {
    func encode() -> JSONValue {
        var object: [String: JSONValue] = ["kind": .string(kind.rawValue)]
        if let fileName { object["fileName"] = .string(fileName) }
        if let relativeToStore { object["relativeToStore"] = .string(relativeToStore) }
        if let origin { object["origin"] = .string(origin) }
        if !needs.isEmpty {
            object["needs"] = .object(needs.mapValues { byName in .object(byName.mapValues { $0.encode() }) })
        }
        if let publish { object["publish"] = publish.encode() }
        if !provenance.isEmpty { object["provenance"] = .object(provenance.mapValues { $0.encode() }) }
        return .object(object)
    }

    static func decode(_ json: JSONValue, name: String) throws -> CollectionsFile.Entry {
        let what = "collection \"\(name)\""
        guard case .object(let object) = json else { throw CollectionsFileError.malformed("\(what) is not a JSON object") }
        let rawKind = try CollectionsFile.requiredString(object["kind"], "\(what) kind")
        guard let kind = CollectionKind(rawValue: rawKind) else {
            throw CollectionsFileError.malformed("\(what) kind \"\(rawKind)\" is not local or synced")
        }
        var needs: [String: [String: CollectionsFile.Need]] = [:]
        for (connector, byName) in try CollectionsFile.objectValue(object["needs"], "\(what) needs") {
            var forConnector: [String: CollectionsFile.Need] = [:]
            for (needName, value) in try CollectionsFile.objectValue(byName, "\(what) needs \"\(connector)\"") {
                forConnector[needName] = try CollectionsFile.Need.decode(value, what: "\(what) need \"\(connector)\".\"\(needName)\"")
            }
            needs[connector] = forConnector
        }
        var provenance: [String: CollectionsFile.Provenance] = [:]
        for (connector, value) in try CollectionsFile.objectValue(object["provenance"], "\(what) provenance") {
            provenance[connector] = try CollectionsFile.Provenance.decode(value, what: "\(what) provenance \"\(connector)\"")
        }
        return CollectionsFile.Entry(
            kind: kind,
            fileName: try CollectionsFile.optionalString(object["fileName"], "\(what) fileName"),
            relativeToStore: try CollectionsFile.optionalString(object["relativeToStore"], "\(what) relativeToStore"),
            origin: try CollectionsFile.optionalString(object["origin"], "\(what) origin"),
            needs: needs,
            publish: try object["publish"].map { try CollectionsFile.PublishRecord.decode($0, what: "\(what) publish") },
            provenance: provenance)
    }
}

extension CollectionsFile.Need {
    func encode() -> JSONValue {
        .object(["hint": hint.map(JSONValue.string) ?? .null, "path": .string(pointer.description)])
    }

    static func decode(_ json: JSONValue, what: String) throws -> CollectionsFile.Need {
        guard case .object(let object) = json else { throw CollectionsFileError.malformed("\(what) is not a JSON object") }
        return CollectionsFile.Need(
            hint: try CollectionsFile.optionalString(object["hint"], "\(what) hint"),
            pointer: try CollectionsFile.pointer(try CollectionsFile.requiredString(object["path"], "\(what) path"), "\(what) path"))
    }
}

extension CollectionsFile.PublishRecord {
    func encode() -> JSONValue {
        var paths: [String: JSONValue] = [:]
        for (connector, marks) in intent.pathMarks {
            var byPointer: [String: JSONValue] = [:]
            for (pointer, mark) in marks {
                byPointer[pointer.description] = .object([
                    "name": .string(mark.name),
                    "hint": mark.hint.map(JSONValue.string) ?? .null,
                ])
            }
            paths[connector] = .object(byPointer)
        }
        return .object([
            "slug": .string(slug),
            "origin": .string(origin),
            // Sorted: a set has no order of its own, and the file must not churn between saves.
            "shareValues": .object(intent.shareValues.mapValues { .array($0.sorted().map(JSONValue.string)) }),
            "paths": .object(paths),
            "hints": .object(intent.hints.mapValues { .object($0.mapValues(JSONValue.string)) }),
        ])
    }

    static func decode(_ json: JSONValue, what: String) throws -> CollectionsFile.PublishRecord {
        guard case .object(let object) = json else { throw CollectionsFileError.malformed("\(what) is not a JSON object") }
        var shareValues: [String: Set<String>] = [:]
        for (connector, value) in try CollectionsFile.objectValue(object["shareValues"], "\(what) shareValues") {
            shareValues[connector] = try CollectionsFile.stringSet(value, "\(what) shareValues \"\(connector)\"")
        }
        var pathMarks: [String: [JSONPointer: PublishIntent.PathMark]] = [:]
        for (connector, value) in try CollectionsFile.objectValue(object["paths"], "\(what) paths") {
            var marks: [JSONPointer: PublishIntent.PathMark] = [:]
            for (rawPointer, mark) in try CollectionsFile.objectValue(value, "\(what) paths \"\(connector)\"") {
                let markWhat = "\(what) path \"\(connector)\".\"\(rawPointer)\""
                guard case .object(let fields) = mark else { throw CollectionsFileError.malformed("\(markWhat) is not a JSON object") }
                marks[try CollectionsFile.pointer(rawPointer, markWhat)] = PublishIntent.PathMark(
                    name: try CollectionsFile.requiredString(fields["name"], "\(markWhat) name"),
                    hint: try CollectionsFile.optionalString(fields["hint"], "\(markWhat) hint"))
            }
            pathMarks[connector] = marks
        }
        var hints: [String: [String: String]] = [:]
        for (connector, value) in try CollectionsFile.objectValue(object["hints"], "\(what) hints") {
            var byName: [String: String] = [:]
            for (key, hint) in try CollectionsFile.objectValue(value, "\(what) hints \"\(connector)\"") {
                byName[key] = try CollectionsFile.requiredString(hint, "\(what) hint \"\(connector)\".\"\(key)\"")
            }
            hints[connector] = byName
        }
        return CollectionsFile.PublishRecord(
            slug: try CollectionsFile.requiredString(object["slug"], "\(what) slug"),
            origin: try CollectionsFile.requiredString(object["origin"], "\(what) origin"),
            intent: PublishIntent(shareValues: shareValues, pathMarks: pathMarks, hints: hints))
    }
}

extension CollectionsFile.Provenance {
    func encode() -> JSONValue {
        var object: [String: JSONValue] = ["from": .string(from), "date": .string(date)]
        if let author { object["author"] = .string(author) }
        return .object(object)
    }

    static func decode(_ json: JSONValue, what: String) throws -> CollectionsFile.Provenance {
        guard case .object(let object) = json else { throw CollectionsFileError.malformed("\(what) is not a JSON object") }
        return CollectionsFile.Provenance(
            from: try CollectionsFile.requiredString(object["from"], "\(what) from"),
            author: try CollectionsFile.optionalString(object["author"], "\(what) author"),
            date: try CollectionsFile.requiredString(object["date"], "\(what) date"))
    }
}

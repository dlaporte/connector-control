import Foundation

/// The bindings only this machine knows: where a synced collection's document actually sits
/// here, what it hashed to the last time it was read, and which folder a published collection
/// writes to. Never synced — the sidecar beside the master list carries what every machine
/// shares.
///
/// Mirror: windows/src/ConnectorControl.Core/CollectionsLocalCache.cs
public struct CollectionsLocalCache: Equatable, Sendable {
    public static let fileName = "collections-local.json"
    public static let formatVersion = 1

    public var synced: [String: SyncedBinding]
    public var published: [String: PublishBinding]
    /// The collection Claude's config was last written from on this machine. Claude's file holds
    /// that collection's connectors, so it is the only one a launch may ingest them into; nil
    /// until the first apply that records it.
    public var lastAppliedCollection: String?

    public init(synced: [String: SyncedBinding], published: [String: PublishBinding],
                lastAppliedCollection: String? = nil) {
        self.synced = synced
        self.published = published
        self.lastAppliedCollection = lastAppliedCollection
    }

    public struct SyncedBinding: Equatable, Sendable {
        /// Absent means the document has not been located on this machine.
        public var path: String?
        public var lastHash: String?
        /// Connector → why this platform skipped it, so the pending diff can leave it out.
        public var excluded: [String: String]
        public init(path: String?, lastHash: String?, excluded: [String: String]) {
            self.path = path
            self.lastHash = lastHash
            self.excluded = excluded
        }
    }

    public struct PublishBinding: Equatable, Sendable {
        public var folder: String
        public var lastWrittenHash: String?
        /// Every path this machine has written into the document as a placeholder, which must
        /// never appear in it as written. Kept here, where nothing syncs it, so the publisher
        /// fails closed whatever the sidecar or the editor says: a publish that happens on its
        /// own only adds to it, and only the author, pressing Publish in the sheet after reading
        /// the preview, replaces it.
        public var markedValues: Set<String>
        /// Paths the author let travel as written in this collection's document, pressing Release
        /// and then Publish in the sheet after reading the preview, although this machine keeps them
        /// back elsewhere: on another collection's list, or as a folder it binds.
        public var releasedValues: Set<String>
        public init(folder: String, lastWrittenHash: String?, markedValues: Set<String> = [],
                    releasedValues: Set<String> = []) {
            self.folder = folder
            self.lastWrittenHash = lastWrittenHash
            self.markedValues = markedValues
            self.releasedValues = releasedValues
        }
    }

    // MARK: Encode

    public func encode() -> JSONValue {
        var root: [String: JSONValue] = [
            "version": .int(Self.formatVersion),
            "synced": .object(synced.mapValues { $0.encode() }),
            "published": .object(published.mapValues { $0.encode() }),
        ]
        if let lastAppliedCollection { root["lastAppliedCollection"] = .string(lastAppliedCollection) }
        return .object(root)
    }

    // MARK: Decode

    public static func decode(_ json: JSONValue) throws -> CollectionsLocalCache {
        guard case .object(let root) = json else { throw CollectionsFileError.malformed("top level is not a JSON object") }
        guard case .int(let version)? = root["version"] else { throw CollectionsFileError.malformed("version is missing") }
        guard version == formatVersion else {
            throw CollectionsFileError.malformed("version \(version) is not \(formatVersion)")
        }
        var synced: [String: SyncedBinding] = [:]
        for (name, value) in try CollectionsFile.objectValue(root["synced"], "synced") {
            synced[name] = try SyncedBinding.decode(value, what: "synced \"\(name)\"")
        }
        var published: [String: PublishBinding] = [:]
        for (name, value) in try CollectionsFile.objectValue(root["published"], "published") {
            published[name] = try PublishBinding.decode(value, what: "published \"\(name)\"")
        }
        return CollectionsLocalCache(
            synced: synced, published: published,
            // Absent in a cache written before it was recorded: the next apply records it.
            lastAppliedCollection: try CollectionsFile.optionalString(root["lastAppliedCollection"], "lastAppliedCollection"))
    }

    // MARK: Disk

    /// Missing or unreadable loads as empty, for the reason `CollectionsFile.load` gives: every
    /// binding here can be found again, and the Locate banner asks for the one that cannot.
    public static func load(from url: URL) -> CollectionsLocalCache {
        guard let data = try? Data(contentsOf: url),
              let json = try? JSONValue.parse(data),
              let cache = try? decode(json) else { return CollectionsLocalCache(synced: [:], published: [:]) }
        return cache
    }

    public func save(to url: URL, staging: URL?) throws {
        try AtomicFile.write(encode().serialized(), to: url, staging: staging)
    }

    /// Drops a binding the sidecar no longer vouches for: a source binding for a collection that
    /// is not synced any more, and a publish folder for one that is not published any more.
    public func reconciled(with file: CollectionsFile) -> CollectionsLocalCache {
        CollectionsLocalCache(
            synced: synced.filter { file.kind(of: $0.key) == .synced },
            published: published.filter { file.collections[$0.key]?.publish != nil },
            lastAppliedCollection: lastAppliedCollection)
    }
}

// MARK: - Nested encode / decode

extension CollectionsLocalCache.SyncedBinding {
    func encode() -> JSONValue {
        var object: [String: JSONValue] = ["excluded": .object(excluded.mapValues(JSONValue.string))]
        if let path { object["path"] = .string(path) }
        if let lastHash { object["lastHash"] = .string(lastHash) }
        return .object(object)
    }

    static func decode(_ json: JSONValue, what: String) throws -> CollectionsLocalCache.SyncedBinding {
        guard case .object(let object) = json else { throw CollectionsFileError.malformed("\(what) is not a JSON object") }
        var excluded: [String: String] = [:]
        for (connector, reason) in try CollectionsFile.objectValue(object["excluded"], "\(what) excluded") {
            excluded[connector] = try CollectionsFile.requiredString(reason, "\(what) excluded \"\(connector)\"")
        }
        return CollectionsLocalCache.SyncedBinding(
            path: try CollectionsFile.optionalString(object["path"], "\(what) path"),
            lastHash: try CollectionsFile.optionalString(object["lastHash"], "\(what) lastHash"),
            excluded: excluded)
    }
}

extension CollectionsLocalCache.PublishBinding {
    func encode() -> JSONValue {
        var object: [String: JSONValue] = ["folder": .string(folder)]
        if let lastWrittenHash { object["lastWrittenHash"] = .string(lastWrittenHash) }
        // Sorted, so the file does not churn between saves that change nothing.
        if !markedValues.isEmpty {
            object["markedValues"] = .array(markedValues.sorted { $0.ordinallyPrecedes($1) }.map(JSONValue.string))
        }
        if !releasedValues.isEmpty {
            object["releasedValues"] = .array(releasedValues.sorted { $0.ordinallyPrecedes($1) }.map(JSONValue.string))
        }
        return .object(object)
    }

    static func decode(_ json: JSONValue, what: String) throws -> CollectionsLocalCache.PublishBinding {
        guard case .object(let object) = json else { throw CollectionsFileError.malformed("\(what) is not a JSON object") }
        return CollectionsLocalCache.PublishBinding(
            folder: try CollectionsFile.requiredString(object["folder"], "\(what) folder"),
            lastWrittenHash: try CollectionsFile.optionalString(object["lastWrittenHash"], "\(what) lastWrittenHash"),
            // Absent in a cache written before the list was kept: nothing marked yet, which the
            // next write fills in.
            markedValues: try CollectionsFile.stringSet(object["markedValues"], "\(what) markedValues"),
            releasedValues: try CollectionsFile.stringSet(object["releasedValues"], "\(what) releasedValues"))
    }
}

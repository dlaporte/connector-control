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
    /// What a collection's publish binding left behind when publishing stopped: this machine's
    /// memory of the paths it kept back and the folders it published into, kept so a collection
    /// published again still refuses them. Starting again takes the record back into the binding
    /// where it is that collection's own; what a collection that has since left the store left
    /// under the name stays beside the binding, since its folders are not the new one's to take.
    public var kept: [String: KeptRecord]
    /// The collection Claude's config was last written from on this machine. Claude's file holds
    /// that collection's connectors, so it is the only one a launch may ingest them into; nil
    /// until the first apply that records it.
    public var lastAppliedCollection: String?
    /// The connector names that apply wrote into Claude's file. They are what
    /// `lastAppliedCollection` rendered, so they still say which entries are its own once the
    /// collection itself is gone — deleted here, or on another machine, which leaves nothing
    /// else behind. nil until the first apply that records them.
    public var lastAppliedNames: Set<String>?

    public init(synced: [String: SyncedBinding], published: [String: PublishBinding],
                kept: [String: KeptRecord] = [:], lastAppliedCollection: String? = nil,
                lastAppliedNames: Set<String>? = nil) {
        self.synced = synced
        self.published = published
        self.kept = kept
        self.lastAppliedCollection = lastAppliedCollection
        self.lastAppliedNames = lastAppliedNames
    }

    /// The lists a stopped publish left behind, as `PublishBinding` holds them while it publishes,
    /// and the one no binding holds: the folders of the collections that bore the name before.
    public struct KeptRecord: Equatable, Sendable {
        public var markedValues: Set<String>
        public var releasedValues: Set<String>
        public var publishedFolders: Set<String>
        /// The folders that collections which once bore this name, and have since left the store,
        /// published into. They are this machine's paths to keep back — refused as a releasable
        /// kept path, named in the sheet as that collection's folder — and never any collection's
        /// own, so `${COLLECTION_DIR}` never stands for them and no publish consumes them. Kept
        /// apart from `publishedFolders` because a Stop would otherwise fold them into the next
        /// collection's own, and withdraw the release the sheet offered for them.
        public var departedFolders: Set<String>
        /// The origin the collection this record belongs to published under, while that collection
        /// is still the one bearing the name. A collection made with a deleted one's name is a
        /// different collection and clears it: the folders the old one left are another
        /// collection's to it, released rather than written over. Absent too in a record written
        /// before origins were kept, which is read the same way.
        public var origin: String?
        public init(markedValues: Set<String> = [], releasedValues: Set<String> = [],
                    publishedFolders: Set<String> = [], departedFolders: Set<String> = [],
                    origin: String? = nil) {
            self.markedValues = markedValues
            self.releasedValues = releasedValues
            self.publishedFolders = publishedFolders
            self.departedFolders = departedFolders
            self.origin = origin
        }

        /// An origin alone says nothing about what must not travel, so a record holding only one
        /// is no record at all.
        public var isEmpty: Bool {
            markedValues.isEmpty && releasedValues.isEmpty && publishedFolders.isEmpty && departedFolders.isEmpty
        }

        /// What a publish binding leaves behind when it goes, merged with anything already
        /// remembered under that name. The binding goes three ways — Stop Publishing, a delete
        /// made here, and a load finding the collection deleted or unpublished on another machine
        /// — and all three leave the same memory of what must not travel.
        ///
        /// The earlier record's folders merge into the binding's own only where the two published
        /// under one origin, none on either side counting as one — a binding and a record written
        /// before origins were kept are read as one collection's. Otherwise the record is another
        /// collection's, one that bore the name and left, and its folders go to `departedFolders`
        /// with whatever it already held there: the next publish must not take them as its own.
        public static func remembering(_ binding: PublishBinding, after earlier: KeptRecord?) -> KeptRecord {
            let own = earlier?.origin == binding.origin
            let earlierFolders = earlier?.publishedFolders ?? []
            return KeptRecord(
                markedValues: binding.markedValues.union(earlier?.markedValues ?? []),
                releasedValues: binding.releasedValues.union(earlier?.releasedValues ?? []),
                publishedFolders: binding.publishedFolders.union([binding.folder])
                    .union(own ? earlierFolders : []),
                departedFolders: (earlier?.departedFolders ?? []).union(own ? [] : earlierFolders),
                origin: binding.origin ?? earlier?.origin)
        }
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
        /// Every folder this binding has published into, the current one included. A connector
        /// can bring an earlier one back as written — a backup taken before the folder moved — and
        /// the author's old folder is no more a subscriber's than the current one.
        public var publishedFolders: Set<String>
        /// The origin the collection publishes under, so what this binding leaves behind still
        /// says whose folders they were once the sidecar entry that named it is gone. Absent in a
        /// binding written before it was kept.
        public var origin: String?
        public init(folder: String, lastWrittenHash: String?, markedValues: Set<String> = [],
                    releasedValues: Set<String> = [], publishedFolders: Set<String> = [],
                    origin: String? = nil) {
            self.folder = folder
            self.lastWrittenHash = lastWrittenHash
            self.markedValues = markedValues
            self.releasedValues = releasedValues
            self.publishedFolders = publishedFolders
            self.origin = origin
        }
    }

    // MARK: Encode

    public func encode() -> JSONValue {
        var root: [String: JSONValue] = [
            "version": .int(Self.formatVersion),
            "synced": .object(synced.mapValues { $0.encode() }),
            "published": .object(published.mapValues { $0.encode() }),
        ]
        let remembered = kept.filter { !$0.value.isEmpty }
        if !remembered.isEmpty { root["kept"] = .object(remembered.mapValues { $0.encode() }) }
        if let lastAppliedCollection { root["lastAppliedCollection"] = .string(lastAppliedCollection) }
        if let lastAppliedNames {
            root["lastAppliedNames"] = .array(lastAppliedNames.sorted { $0.ordinallyPrecedes($1) }.map(JSONValue.string))
        }
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
        var kept: [String: KeptRecord] = [:]
        for (name, value) in try CollectionsFile.objectValue(root["kept"], "kept") {
            kept[name] = try KeptRecord.decode(value, what: "kept \"\(name)\"")
        }
        // Absent, rather than empty, in a cache written before they were recorded: an apply that
        // rendered nothing records an empty list, which is not the same thing.
        var lastAppliedNames: Set<String>?
        if root["lastAppliedNames"] != nil {
            lastAppliedNames = try CollectionsFile.stringSet(root["lastAppliedNames"], "lastAppliedNames")
        }
        return CollectionsLocalCache(
            synced: synced, published: published, kept: kept,
            // Absent in a cache written before it was recorded: the next apply records it.
            lastAppliedCollection: try CollectionsFile.optionalString(root["lastAppliedCollection"], "lastAppliedCollection"),
            lastAppliedNames: lastAppliedNames)
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
    /// What a stopped publish left behind is not a binding the sidecar vouches for, and is kept
    /// whatever it says: it is this machine's memory of what must not travel.
    ///
    /// A publish binding dropped here is a collection deleted, or stopped, on another machine,
    /// which is how a collection disappears from a store that syncs. It leaves what stopping it
    /// here leaves: the paths it kept back, the paths the author released and the folders it
    /// published into. Nothing else on this machine remembers them — there is no record to union,
    /// and the sidecar entry that carried its marks went with it.
    public func reconciled(with file: CollectionsFile) -> CollectionsLocalCache {
        let vouched = published.filter { file.collections[$0.key]?.publish != nil }
        var remembered = kept
        for (name, binding) in published where vouched[name] == nil {
            let record = KeptRecord.remembering(binding, after: kept[name])
            if !record.isEmpty { remembered[name] = record }
        }
        return CollectionsLocalCache(
            synced: synced.filter { file.kind(of: $0.key) == .synced },
            published: vouched,
            kept: remembered,
            lastAppliedCollection: lastAppliedCollection,
            lastAppliedNames: lastAppliedNames)
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
        if !publishedFolders.isEmpty {
            object["publishedFolders"] = .array(publishedFolders.sorted { $0.ordinallyPrecedes($1) }.map(JSONValue.string))
        }
        if let origin { object["origin"] = .string(origin) }
        return .object(object)
    }

    static func decode(_ json: JSONValue, what: String) throws -> CollectionsLocalCache.PublishBinding {
        guard case .object(let object) = json else { throw CollectionsFileError.malformed("\(what) is not a JSON object") }
        let folder = try CollectionsFile.requiredString(object["folder"], "\(what) folder")
        return CollectionsLocalCache.PublishBinding(
            folder: folder,
            lastWrittenHash: try CollectionsFile.optionalString(object["lastWrittenHash"], "\(what) lastWrittenHash"),
            // Absent in a cache written before the list was kept: nothing marked yet, which the
            // next write fills in.
            markedValues: try CollectionsFile.stringSet(object["markedValues"], "\(what) markedValues"),
            releasedValues: try CollectionsFile.stringSet(object["releasedValues"], "\(what) releasedValues"),
            // The folder a binding names is one it publishes into, whether or not the list says so:
            // a binding written before the list was kept knows that much about itself.
            publishedFolders: try CollectionsFile.stringSet(object["publishedFolders"], "\(what) publishedFolders")
                .union([folder]),
            // Absent in a binding written before the origin was kept: the next publish fills it in.
            origin: try CollectionsFile.optionalString(object["origin"], "\(what) origin"))
    }
}

extension CollectionsLocalCache.KeptRecord {
    func encode() -> JSONValue {
        var object: [String: JSONValue] = [:]
        for (key, values) in [("markedValues", markedValues), ("releasedValues", releasedValues),
                              ("publishedFolders", publishedFolders), ("departedFolders", departedFolders)]
        where !values.isEmpty {
            object[key] = .array(values.sorted { $0.ordinallyPrecedes($1) }.map(JSONValue.string))
        }
        if let origin { object["origin"] = .string(origin) }
        return .object(object)
    }

    static func decode(_ json: JSONValue, what: String) throws -> CollectionsLocalCache.KeptRecord {
        let object = try CollectionsFile.objectValue(json, what)
        return CollectionsLocalCache.KeptRecord(
            markedValues: try CollectionsFile.stringSet(object["markedValues"], "\(what) markedValues"),
            releasedValues: try CollectionsFile.stringSet(object["releasedValues"], "\(what) releasedValues"),
            publishedFolders: try CollectionsFile.stringSet(object["publishedFolders"], "\(what) publishedFolders"),
            // Absent in a record written before they were kept apart: nothing departed yet.
            departedFolders: try CollectionsFile.stringSet(object["departedFolders"], "\(what) departedFolders"),
            origin: try CollectionsFile.optionalString(object["origin"], "\(what) origin"))
    }
}

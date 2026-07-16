import Foundation

public enum EditView: String, Codable {
    case form, json
}

public struct MCPEntry: Equatable, Codable {
    public var enabled: Bool
    public var config: JSONValue
    public var lastEditView: EditView

    public init(enabled: Bool = true, config: JSONValue, lastEditView: EditView = .form) {
        self.enabled = enabled
        self.config = config
        self.lastEditView = lastEditView
    }
}

public struct MasterStore: Equatable, Codable {
    public var version: Int
    public var mcps: [String: MCPEntry]

    public static let empty = MasterStore(version: 1, mcps: [:])

    public init(version: Int, mcps: [String: MCPEntry]) {
        self.version = version
        self.mcps = mcps
    }
}

public enum MasterStoreIO {
    /// Missing file → empty store. Corrupt file → moved aside to
    /// `mcps.corrupt.<timestamp>.json` (returned) and an empty store; the caller
    /// repopulates it by reconciling against Claude's config.
    public static func load(
        from url: URL, now: Date = Date()
    ) -> (store: MasterStore, corruptFileURL: URL?) {
        let fm = FileManager.default
        guard fm.fileExists(atPath: url.path) else { return (.empty, nil) }
        do {
            let data = try Data(contentsOf: url)
            let store = try JSONDecoder().decode(MasterStore.self, from: data)
            return (store, nil)
        } catch {
            let stamp = BackupTimestamp.string(from: now)
            let aside = url.deletingLastPathComponent()
                .appendingPathComponent("mcps.corrupt.\(stamp).json")
            do {
                try fm.moveItem(at: url, to: aside)
                return (.empty, aside)
            } catch {
                // Couldn't move it aside; the corrupt file stays in place.
                return (.empty, url)
            }
        }
    }

    public static func save(_ store: MasterStore, to url: URL) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try AtomicFile.write(try encoder.encode(store), to: url)
    }
}

public enum BackupTimestamp {
    public static func string(from date: Date) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = .current
        f.dateFormat = "yyyy-MM-dd'T'HH-mm-ss-SSS"
        return f.string(from: date)
    }
}

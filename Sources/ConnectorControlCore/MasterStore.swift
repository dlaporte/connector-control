import Foundation

public enum EditView: String, Codable, Hashable {
    case form, json
}

public struct MCPEntry: Equatable, Hashable, Codable {
    public var enabled: Bool
    public var config: JSONValue
    public var lastEditView: EditView

    public init(enabled: Bool = true, config: JSONValue, lastEditView: EditView = .form) {
        self.enabled = enabled
        self.config = config
        self.lastEditView = lastEditView
    }
}

/// A full, independent snapshot of connectors: its own configs + enabled flags.
public struct Profile: Equatable, Codable {
    public var mcps: [String: MCPEntry]
    public init(mcps: [String: MCPEntry] = [:]) { self.mcps = mcps }
}

/// Schema v2 only — no v1 fallback. A v1 (or otherwise malformed) file on
/// disk fails to decode and is handled by `MasterStoreIO.load`'s existing
/// corrupt-file path: moved aside and rebuilt fresh from Claude's config.
public struct MasterStore: Equatable, Codable {
    public var version: Int
    public var activeProfile: String
    public var profiles: [String: Profile]

    /// The active profile's connectors — the view the entire app operates on.
    public var mcps: [String: MCPEntry] {
        get { profiles[activeProfile]?.mcps ?? [:] }
        set { profiles[activeProfile, default: Profile()].mcps = newValue }
    }

    public static let empty = MasterStore(
        version: 2, activeProfile: "Default",
        profiles: ["Default": Profile()])

    public init(version: Int, activeProfile: String, profiles: [String: Profile]) {
        self.version = version
        self.activeProfile = activeProfile
        self.profiles = profiles
    }

    /// Convenience used across existing tests: a single-profile store. The
    /// `version` parameter is ignored/normalized — the store is always v2.
    public init(version: Int, mcps: [String: MCPEntry]) {
        self.init(version: 2, activeProfile: "Default",
                   profiles: ["Default": Profile(mcps: mcps)])
    }

    /// nil on success, else a user-facing error message.
    public mutating func addProfile(named name: String, copyingCurrent: Bool) -> String? {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return "Name must not be empty." }
        guard profiles[trimmed] == nil else {
            return "A profile named \u{201C}\(trimmed)\u{201D} already exists."
        }
        profiles[trimmed] = copyingCurrent ? Profile(mcps: mcps) : Profile()
        activeProfile = trimmed
        return nil
    }

    public mutating func renameActiveProfile(to name: String) -> String? {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return "Name must not be empty." }
        if trimmed != activeProfile, profiles[trimmed] != nil {
            return "A profile named \u{201C}\(trimmed)\u{201D} already exists."
        }
        guard let current = profiles.removeValue(forKey: activeProfile) else { return nil }
        profiles[trimmed] = current
        activeProfile = trimmed
        return nil
    }

    public mutating func deleteActiveProfile() -> String? {
        guard profiles.count > 1 else { return "Can\u{2019}t delete the last profile." }
        profiles.removeValue(forKey: activeProfile)
        activeProfile = profiles.keys.sorted().first!
        return nil
    }

    public mutating func switchProfile(to name: String) -> String? {
        guard profiles[name] != nil else {
            return "No profile named \u{201C}\(name)\u{201D}."
        }
        activeProfile = name
        return nil
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
            var store = try JSONDecoder().decode(MasterStore.self, from: data)
            // Self-heal a decoded-but-inconsistent activeProfile (hand-edited
            // or corrupted file) — never crash; fall back to an existing
            // profile (sorted first), or a fresh Default if none remain.
            if store.profiles[store.activeProfile] == nil {
                if let fallback = store.profiles.keys.sorted().first {
                    store.activeProfile = fallback
                } else {
                    store.profiles["Default"] = Profile()
                    store.activeProfile = "Default"
                }
            }
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

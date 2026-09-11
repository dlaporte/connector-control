import Foundation

/// The lexicographically-sortable stamp used in every backup and corrupt-file
/// aside name (`BackupManager`, `MasterStoreIO.load`).
public enum BackupTimestamp {
    /// UTC, not local time: backup recency is derived from a lexicographic
    /// sort of these stamps, and local wall-clock repeats an hour every DST
    /// fall-back — during which newer backups would sort older, breaking
    /// dedup's newest-snapshot comparison and prune's keep-newest contract.
    /// Built once: DateFormatter's own setup is not free, and every call here
    /// only reads it.
    private static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "UTC")
        f.dateFormat = "yyyy-MM-dd'T'HH-mm-ss-SSS'Z'"
        return f
    }()

    public static func string(from date: Date) -> String {
        formatter.string(from: date)
    }
}

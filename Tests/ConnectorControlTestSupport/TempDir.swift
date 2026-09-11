import Foundation

/// A unique temp directory deleted on `dispose()` (the C# TempDir). Shared by
/// both test targets so a Core or State test can get an isolated on-disk
/// fixture without hand-rolling `NSTemporaryDirectory()` bookkeeping.
public final class TempDir {
    public let url: URL

    public init(prefix: String = "cc") {
        url = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("\(prefix)-\(UUID().uuidString)")
        try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    }

    /// A file or directory inside this temp dir (not created).
    public func file(_ relative: String) -> URL { url.appendingPathComponent(relative) }

    public func dispose() { try? FileManager.default.removeItem(at: url) }
}

public extension TempDir {
    /// Writes `content` to `url` (creating intermediate directories as
    /// needed) and advances its modification date one second past whatever
    /// the write just produced — deterministically later than anything
    /// written before it, without sleeping wall-clock time to get there.
    /// Replaces `Thread.sleep` used only to keep a file's mtime distinct from
    /// an earlier write a watcher is comparing against.
    static func touch(_ url: URL, _ content: String = "") throws {
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(content.utf8).write(to: url)
        try bumpModificationDate(of: url)
    }

    /// Sets `url`'s modification date one second past its current one — the
    /// half of `touch` a caller needs on its own when the write itself must
    /// go through a different path (e.g. an atomic replace).
    static func bumpModificationDate(of url: URL) throws {
        let fm = FileManager.default
        let attributes = try? fm.attributesOfItem(atPath: url.path)
        let current = (attributes?[.modificationDate] as? Date) ?? Date()
        try fm.setAttributes([.modificationDate: current.addingTimeInterval(1)], ofItemAtPath: url.path)
    }
}

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

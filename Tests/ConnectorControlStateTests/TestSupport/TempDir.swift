import Foundation

/// A unique temp directory deleted on `dispose()` (the C# TempDir).
final class TempDir {
    let url: URL

    init(prefix: String = "cc") {
        url = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("\(prefix)-\(UUID().uuidString)")
        try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    }

    /// A file or directory inside this temp dir (not created).
    func file(_ relative: String) -> URL { url.appendingPathComponent(relative) }

    func dispose() { try? FileManager.default.removeItem(at: url) }
}

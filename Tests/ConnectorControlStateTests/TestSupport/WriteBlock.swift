import Foundation

/// Makes writes to one file fail while reads keep working: the parent
/// directory goes to mode 0500, so AtomicFile cannot create its temp file and
/// ConfigService.apply throws. `isEffective` is asserted, never branched on:
/// it is false only when the suite runs as root, which mode bits do not bind.
final class WriteBlock {
    private let directory: URL
    private let previousMode: Int
    let isEffective: Bool

    init(_ url: URL) throws {
        directory = url.deletingLastPathComponent()
        let fm = FileManager.default
        guard let mode = try fm.attributesOfItem(atPath: directory.path)[.posixPermissions] as? Int else {
            throw CocoaError(.fileReadUnknown)
        }
        previousMode = mode
        try fm.setAttributes([.posixPermissions: 0o500], ofItemAtPath: directory.path)
        let probe = directory.appendingPathComponent(".writeblock-probe-\(UUID().uuidString)")
        isEffective = (try? Data().write(to: probe)) == nil
        try? fm.removeItem(at: probe)
    }

    /// Restores the directory's mode. Safe to call twice.
    func dispose() {
        try? FileManager.default.setAttributes([.posixPermissions: previousMode], ofItemAtPath: directory.path)
    }
}

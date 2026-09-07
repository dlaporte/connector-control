import Foundation

public enum AtomicFile {
    public static func write(_ data: Data, to url: URL) throws {
        let fm = FileManager.default
        let dir = url.deletingLastPathComponent()
        // A directory this call has to create holds a private file, so it is
        // owner-only from the start (the sweep would only catch it on the
        // next launch; Windows applies its ACL the same way). Directories that
        // already exist — a synced folder the user chose — are left alone.
        try fm.createDirectory(at: dir, withIntermediateDirectories: true,
                               attributes: [.posixPermissions: 0o700])
        let tmp = dir.appendingPathComponent(".\(url.lastPathComponent).tmp-\(UUID().uuidString)")
        try data.write(to: tmp)
        // Connector configs can hold env-var secrets; never leave them
        // world-readable (the default umask yields 644). A rename keeps the
        // temp file's mode, but replaceItemAt does NOT by default: it restores
        // the destination's old metadata, so a 644 file Claude Desktop created
        // would stay 644 through every write. .usingNewMetadataOnly keeps the
        // 600 set here.
        try? fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: tmp.path)
        defer { try? fm.removeItem(at: tmp) }
        if fm.fileExists(atPath: url.path) {
            _ = try fm.replaceItemAt(url, withItemAt: tmp, options: .usingNewMetadataOnly)
        } else {
            do {
                try fm.moveItem(at: tmp, to: url)
            } catch where fm.fileExists(atPath: url.path) {
                _ = try fm.replaceItemAt(url, withItemAt: tmp, options: .usingNewMetadataOnly)
            }
        }
    }
}

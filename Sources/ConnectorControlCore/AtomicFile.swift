import Foundation

public enum AtomicFile {
    public static func write(_ data: Data, to url: URL) throws {
        let fm = FileManager.default
        // A symlinked destination (a config kept in a dotfiles repo) is written
        // through: the temp file is created beside the real file and renamed
        // over it, so the link survives and the bytes land where every other
        // reader of the link finds them. Components that do not exist yet are
        // left as given.
        let target = url.resolvingSymlinksInPath()
        let dir = target.deletingLastPathComponent()
        // A directory this call has to create holds a private file, so it is
        // owner-only from the start (the sweep would only catch it on the
        // next launch; Windows applies its ACL the same way). Directories that
        // already exist — a synced folder the user chose — are left alone.
        try fm.createDirectory(at: dir, withIntermediateDirectories: true,
                               attributes: [.posixPermissions: 0o700])
        let tmp = dir.appendingPathComponent(".\(target.lastPathComponent).tmp-\(UUID().uuidString)")
        // Connector configs can hold env-var secrets. The file is created 0600
        // by open(2) itself — never with the umask's default and a chmod after
        // the bytes are already on disk — so there is no instant at which
        // another account could read it. O_EXCL: the name is fresh, so
        // anything already there is an error, not something to overwrite.
        let fd = open(tmp.path, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0o600)
        guard fd >= 0 else {
            throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno),
                          userInfo: [NSFilePathErrorKey: tmp.path])
        }
        defer { try? fm.removeItem(at: tmp) }
        let handle = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
        try handle.write(contentsOf: data)
        try handle.close()
        // The umask can only clear bits, so the mode is already 0600 or
        // tighter; this makes it exactly 0600 and, unlike the old try?, lets
        // a failure surface rather than shipping a file of unknown mode. A
        // rename keeps the temp file's mode, but replaceItemAt does NOT by
        // default: it restores the destination's old metadata, so a 644 file
        // Claude Desktop created would stay 644 through every write.
        // .usingNewMetadataOnly keeps the 600 set here.
        try fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: tmp.path)
        if fm.fileExists(atPath: target.path) {
            _ = try fm.replaceItemAt(target, withItemAt: tmp, options: .usingNewMetadataOnly)
        } else {
            do {
                try fm.moveItem(at: tmp, to: target)
            } catch where fm.fileExists(atPath: target.path) {
                _ = try fm.replaceItemAt(target, withItemAt: tmp, options: .usingNewMetadataOnly)
            }
        }
    }
}

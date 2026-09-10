import Foundation

public enum AtomicFile {
    /// The app's own private folder for temp files (`…/Connector Control/.staging`), set once at
    /// startup. When the target folder is on the same volume, temp files are created here and
    /// renamed into place: a rename does not re-inherit ACEs, so a shared folder's principal
    /// never sees the file, not even empty. Nil (tests, tools) means temp files are created
    /// beside the target and stripped of ACEs before the first write.
    nonisolated(unsafe) public static var privateStagingDirectory: URL?

    /// `staging` when it can serve `dir` — it exists (created 0700 with no ACL) and shares
    /// `dir`'s device, so the final rename stays a rename — else nil.
    static func stagingLocation(for dir: URL, staging: URL?) -> URL? {
        guard let staging else { return nil }
        let fm = FileManager.default
        if !fm.fileExists(atPath: staging.path) {
            guard (try? fm.createDirectory(at: staging, withIntermediateDirectories: true,
                                           attributes: [.posixPermissions: 0o700])) != nil,
                  (try? stripACL(atPath: staging.path)) != nil else { return nil }
        }
        var a = stat(), b = stat()
        guard stat(dir.path, &a) == 0, stat(staging.path, &b) == 0, a.st_dev == b.st_dev else { return nil }
        return staging
    }

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
        let dirExisted = fm.fileExists(atPath: dir.path)
        try fm.createDirectory(at: dir, withIntermediateDirectories: true,
                               attributes: [.posixPermissions: 0o700])
        if !dirExisted {
            // A folder with an inheritable ACE hands one to every child, directories
            // included; 0700 alone would leave the new directory listable by that principal.
            try stripACL(atPath: dir.path)
        }
        let tmpDir = stagingLocation(for: dir, staging: privateStagingDirectory) ?? dir
        let tmp = tmpDir.appendingPathComponent(".\(target.lastPathComponent).tmp-\(UUID().uuidString)")
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
        // The handle owns the descriptor from here, so a throw below closes it.
        let handle = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
        // Before the first byte lands: an inherited ACE grants read regardless of the 0600.
        try stripACL(fd: handle.fileDescriptor)
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

    /// Removes every ACL entry from the object `fd` refers to. `open(…, 0600)` cannot refuse
    /// the ACEs a parent folder hands down, and macOS consults ACEs before the mode bits, so
    /// a file in a shared folder is only private once its ACL is gone.
    public static func stripACL(fd: Int32) throws {
        guard let empty = acl_init(0) else { throw posixError() }
        defer { acl_free(UnsafeMutableRawPointer(empty)) }
        guard acl_set_fd_np(fd, empty, ACL_TYPE_EXTENDED) == 0 else { throw posixError() }
    }

    /// `stripACL(fd:)` for a path: directories, and the sweep's repair of existing files.
    public static func stripACL(atPath path: String) throws {
        guard let empty = acl_init(0) else { throw posixError() }
        defer { acl_free(UnsafeMutableRawPointer(empty)) }
        guard acl_set_link_np(path, ACL_TYPE_EXTENDED, empty) == 0 else { throw posixError() }
    }

    /// True when the object carries any ACL entry (inherited or explicit).
    public static func hasACL(atPath path: String) -> Bool {
        guard let acl = acl_get_link_np(path, ACL_TYPE_EXTENDED) else { return false }
        acl_free(UnsafeMutableRawPointer(acl))
        return true
    }

    private static func posixError() -> NSError {
        NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
    }
}

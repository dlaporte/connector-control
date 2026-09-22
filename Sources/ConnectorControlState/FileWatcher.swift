import Foundation

/// Watches the parent directory of `url` (atomic writes replace the inode, so
/// watching the directory catches create/rename/delete) AND the file's own
/// descriptor (so an in-place truncate+write with no rename still fires).
/// Fires when the file's modification date changes.
///
/// Everything runs on a private serial queue; a confirmed change is handed to
/// `marshal`, which posts `onChange` to the main actor. The
/// callback re-checks on delivery and is dropped if the watcher was stopped or
/// restarted in the meantime — a `stop()` that raced the check wins.
public final class FileWatcher: @unchecked Sendable {
    private let url: URL
    private let onChange: MainActorAction
    private let marshal: @Sendable (@escaping MainActorAction) -> Void
    private let queue = DispatchQueue(label: "com.dlaporte.connector-control.filewatcher")
    private var dirSource: DispatchSourceFileSystemObject?
    /// The descriptor `dirSource` watches, kept so its identity can be compared
    /// with whatever the path resolves to now.
    private var dirFD: Int32 = -1
    private var fileSource: DispatchSourceFileSystemObject?
    private var lastModified: Date?
    private var generation = 0

    public init(url: URL,
                marshal: @escaping @Sendable (@escaping MainActorAction) -> Void,
                onChange: @escaping MainActorAction) {
        self.url = url
        self.marshal = marshal
        self.onChange = onChange
    }

    deinit {
        // No queue.sync here: the last reference may drop inside an event
        // handler on `queue`, and nothing else can reach these fields now.
        dirSource?.cancel()
        fileSource?.cancel()
    }

    /// True while the parent-directory source is live AND still watching the
    /// directory the path resolves to. A directory replaced wholesale — deleted
    /// and recreated, or swapped in by a sync client — leaves the descriptor on
    /// an orphaned inode where nothing under the path can fire again, and
    /// calling that armed would make the caller's re-arm a no-op forever.
    /// A parent that is simply missing stays armed until the event handler sees
    /// it and disarms deliberately, which is a different case with its own
    /// callback.
    public var isArmed: Bool { queue.sync { dirSource != nil && !directoryWasReplaced() } }

    /// Arms the watcher. A no-op while armed on the directory the path resolves
    /// to; when that directory has been replaced, the stale descriptor is
    /// swapped for one on the new directory, which is how a watcher recovers
    /// from a folder a sync client replaced wholesale. Returns without arming
    /// when the parent directory cannot be opened; the caller retries on its
    /// next reload.
    public func start() {
        queue.sync {
            let cold = dirSource == nil
            guard cold || directoryWasReplaced() else { return }
            // A replacement keeps the last-seen modification date, so the first
            // comparison against the new directory's file reports the
            // difference instead of silently baselining it away.
            if cold { lastModified = modificationDate() }
            guard armDirSource() else { return }
            // Only a cold start is a new generation. A replacement stops
            // nothing, and a callback already posted describes a change the
            // caller still has to see.
            if cold { generation += 1 }
            armFileSource()
        }
    }

    public func stop() {
        queue.sync {
            dirSource?.cancel()
            dirSource = nil
            dirFD = -1
            fileSource?.cancel()
            fileSource = nil
            generation += 1
        }
    }

    // MARK: on `queue`

    /// Opens the parent directory and arms the source on it, replacing whatever
    /// source is there. False when the directory cannot be opened.
    private func armDirSource() -> Bool {
        dirSource?.cancel()
        dirSource = nil
        dirFD = -1
        let fd = open(url.deletingLastPathComponent().path, O_EVTONLY)
        guard fd >= 0 else { return false }
        let source = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: fd, eventMask: [.write, .rename, .delete], queue: queue)
        source.setEventHandler { [weak self] in self?.checkForChange() }
        source.setCancelHandler { close(fd) }
        source.resume()
        dirSource = source
        dirFD = fd
        return true
    }

    /// Whether the directory the path resolves to now is a different one from
    /// the descriptor's. Device plus inode is the fingerprint: a directory
    /// recreated at the same path is a new inode, and events for it never reach
    /// a descriptor still holding the old one. A path that resolves to nothing
    /// is not a replacement — that is the deleted-directory case.
    private func directoryWasReplaced() -> Bool {
        guard dirFD >= 0 else { return false }
        var held = stat()
        var atPath = stat()
        guard fstat(dirFD, &held) == 0,
              stat(url.deletingLastPathComponent().path, &atPath) == 0 else { return false }
        return held.st_dev != atPath.st_dev || held.st_ino != atPath.st_ino
    }

    /// The file source must be re-armed whenever the file is atomically
    /// replaced, because the old descriptor then points at the orphaned inode.
    private func armFileSource() {
        fileSource?.cancel()
        fileSource = nil
        let fd = open(url.path, O_EVTONLY)
        guard fd >= 0 else { return }
        let source = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: fd, eventMask: [.write, .extend, .rename, .delete], queue: queue)
        source.setEventHandler { [weak self] in self?.checkForChange() }
        source.setCancelHandler { close(fd) }
        source.resume()
        fileSource = source
    }

    private func checkForChange() {
        guard dirSource != nil else { return }
        guard FileManager.default.fileExists(atPath: url.deletingLastPathComponent().path) else {
            // The watched directory itself is gone. Disarm now — the next
            // start() (AppState retries on every reload) finds it still
            // missing and stays unarmed until the directory reappears. The
            // final callback below cannot use isLive(generation:): that
            // checks dirSource != nil, which is already false by the time it
            // runs because this is the deliberate disarm, not a stale one.
            dirSource?.cancel()
            dirSource = nil
            dirFD = -1
            fileSource?.cancel()
            fileSource = nil
            generation += 1
            marshal { [weak self] in
                guard let self else { return }
                self.onChange()
            }
            return
        }
        armFileSource()
        let current = modificationDate()
        guard current != lastModified else { return }
        lastModified = current
        let observed = generation
        marshal { [weak self] in
            guard let self, self.isLive(generation: observed) else { return }
            self.onChange()
        }
    }

    private func isLive(generation observed: Int) -> Bool {
        queue.sync { dirSource != nil && generation == observed }
    }

    private func modificationDate() -> Date? {
        (try? FileManager.default.attributesOfItem(atPath: url.path))?[.modificationDate] as? Date
    }
}

import Foundation

/// Watches the parent directory of `url` (atomic writes replace the inode, so
/// watching the directory catches create/rename/delete) AND the file's own
/// descriptor (so an in-place truncate+write with no rename still fires).
/// Fires when the file's modification date changes (catalog §6.2).
///
/// Everything runs on a private serial queue; a confirmed change is handed to
/// `marshal`, which posts `onChange` to the main actor (port design §7.6). The
/// callback re-checks on delivery and is dropped if the watcher was stopped or
/// restarted in the meantime — a `stop()` that raced the check wins.
public final class FileWatcher: @unchecked Sendable {
    private let url: URL
    private let onChange: MainActorAction
    private let marshal: @Sendable (@escaping MainActorAction) -> Void
    private let queue = DispatchQueue(label: "com.dlaporte.connector-control.filewatcher")
    private var dirSource: DispatchSourceFileSystemObject?
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

    /// True while the parent-directory source is live.
    public var isArmed: Bool { queue.sync { dirSource != nil } }

    /// Arms the watcher. A no-op while armed. Returns without arming when the
    /// parent directory cannot be opened; the caller retries on its next reload.
    public func start() {
        queue.sync {
            guard dirSource == nil else { return }
            lastModified = modificationDate()
            let dirFD = open(url.deletingLastPathComponent().path, O_EVTONLY)
            guard dirFD >= 0 else { return }
            let source = DispatchSource.makeFileSystemObjectSource(
                fileDescriptor: dirFD, eventMask: [.write, .rename, .delete], queue: queue)
            source.setEventHandler { [weak self] in self?.checkForChange() }
            source.setCancelHandler { close(dirFD) }
            source.resume()
            dirSource = source
            generation += 1
            armFileSource()
        }
    }

    public func stop() {
        queue.sync {
            dirSource?.cancel()
            dirSource = nil
            fileSource?.cancel()
            fileSource = nil
            generation += 1
        }
    }

    // MARK: on `queue`

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

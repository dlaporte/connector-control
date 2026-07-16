import Foundation

/// Watches the parent directory of `url` (atomic writes replace the inode, so
/// watching the directory catches create/rename/delete) AND the file's own FD
/// (so an in-place truncate+write with no rename, e.g. `fs.writeFileSync`,
/// still fires). Fires when the file's modification date changes.
final class FileWatcher {
    private let url: URL
    private let onChange: () -> Void
    private var dirSource: DispatchSourceFileSystemObject?
    private var fileSource: DispatchSourceFileSystemObject?
    private var lastModified: Date?

    init(url: URL, onChange: @escaping () -> Void) {
        self.url = url
        self.onChange = onChange
    }

    deinit { stop() }

    func start() {
        stop()
        lastModified = modificationDate()
        let dirFD = open(url.deletingLastPathComponent().path, O_EVTONLY)
        if dirFD >= 0 {
            let source = DispatchSource.makeFileSystemObjectSource(
                fileDescriptor: dirFD, eventMask: [.write, .rename, .delete],
                queue: .main)
            source.setEventHandler { [weak self] in self?.checkForChange() }
            source.setCancelHandler { close(dirFD) }
            source.resume()
            dirSource = source
        }
        armFileSource()
    }

    func stop() {
        dirSource?.cancel()
        dirSource = nil
        fileSource?.cancel()
        fileSource = nil
    }

    /// The file source must be re-armed whenever the file is atomically
    /// replaced, because the old FD then points at the orphaned inode.
    private func armFileSource() {
        fileSource?.cancel()
        fileSource = nil
        let fd = open(url.path, O_EVTONLY)
        guard fd >= 0 else { return }
        let source = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: fd, eventMask: [.write, .extend, .rename, .delete],
            queue: .main)
        source.setEventHandler { [weak self] in self?.checkForChange() }
        source.setCancelHandler { close(fd) }
        source.resume()
        fileSource = source
    }

    private func checkForChange() {
        armFileSource()
        let current = modificationDate()
        guard current != lastModified else { return }
        lastModified = current
        onChange()
    }

    private func modificationDate() -> Date? {
        (try? FileManager.default.attributesOfItem(atPath: url.path))?[.modificationDate]
            as? Date
    }
}

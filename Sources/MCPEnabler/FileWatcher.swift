import Foundation

/// Watches the parent directory of `url` (atomic writes replace the inode, so
/// watching the file itself misses them) and fires when the file's modification
/// date changes.
final class FileWatcher {
    private let url: URL
    private let onChange: () -> Void
    private var source: DispatchSourceFileSystemObject?
    private var lastModified: Date?

    init(url: URL, onChange: @escaping () -> Void) {
        self.url = url
        self.onChange = onChange
    }

    func start() {
        stop()
        lastModified = modificationDate()
        let dirFD = open(url.deletingLastPathComponent().path, O_EVTONLY)
        guard dirFD >= 0 else { return }
        let source = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: dirFD, eventMask: [.write, .rename, .delete],
            queue: .main)
        source.setEventHandler { [weak self] in self?.checkForChange() }
        source.setCancelHandler { close(dirFD) }
        source.resume()
        self.source = source
    }

    func stop() {
        source?.cancel()
        source = nil
    }

    private func checkForChange() {
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

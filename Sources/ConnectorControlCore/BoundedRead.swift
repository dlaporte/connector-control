import Foundation

/// Why a bounded read refused a file it opened. Platform idiom: an error the caller matches by case;
/// the Windows mirror throws `BoundedReadException`, an `IOException`, so the I/O catches already
/// there take it.
public enum BoundedReadError: Error, Equatable, LocalizedError {
    case notAFile
    case tooLarge

    public var errorDescription: String? {
        switch self {
        case .notAFile: return BoundedRead.notAFileError
        case .tooLarge: return BoundedRead.tooLargeError
        }
    }
}

/// Reads a file someone else may be changing, in one go and at most so many bytes. A size checked
/// beforehand says nothing about what a later read finds: the file can be swapped in between for a
/// far larger one, or for something that is not a file at all. So the file is opened first, and
/// what the open handle is, and how much it holds, decide the read: a regular file of at most
/// `limit` bytes, and never more than `limit + 1` read to find out.
///
/// Mirror: windows/src/ConnectorControl.Core/BoundedRead.cs
public enum BoundedRead {
    public static let notAFileError = "It isn’t a file."
    public static let tooLargeError = "The file is too large."

    public static func data(at url: URL, limit: Int) throws -> Data {
        // Platform idiom: open(2) without waiting, so a FIFO cannot hold the read open, and the
        // descriptor's own fstat; the Windows mirror opens a handle and asks it.
        let descriptor = open(url.path, O_RDONLY | O_NONBLOCK | O_CLOEXEC)
        guard descriptor >= 0 else { throw openError(errno, url) }
        defer { close(descriptor) }
        var info = stat()
        guard fstat(descriptor, &info) == 0 else { throw openError(errno, url) }
        guard info.st_mode & S_IFMT == S_IFREG else { throw BoundedReadError.notAFile }
        guard info.st_size <= Int64(limit) else { throw BoundedReadError.tooLarge }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 64 * 1024)
        // One byte past the limit is enough to know the file grew past it.
        while data.count <= limit {
            let wanted = min(buffer.count, limit + 1 - data.count)
            let count = buffer.withUnsafeMutableBytes { read(descriptor, $0.baseAddress, wanted) }
            if count < 0 {
                if errno == EINTR { continue }
                throw openError(errno, url)
            }
            if count == 0 { break }
            data.append(contentsOf: buffer[0..<count])
        }
        guard data.count <= limit else { throw BoundedReadError.tooLarge }
        return data
    }

    /// The error `Data(contentsOf:)` reports for the same failure, so a message the user sees for a
    /// missing or forbidden file is the system's, as it was before reads were bounded.
    private static func openError(_ code: Int32, _ url: URL) -> Error {
        let kind: CocoaError.Code
        switch code {
        case ENOENT, ENOTDIR: kind = .fileReadNoSuchFile
        case EACCES, EPERM: kind = .fileReadNoPermission
        default: kind = .fileReadUnknown
        }
        var userInfo: [String: Any] = [NSFilePathErrorKey: url.path]
        if let posix = POSIXErrorCode(rawValue: code) { userInfo[NSUnderlyingErrorKey] = POSIXError(posix) }
        return CocoaError(kind, userInfo: userInfo)
    }
}

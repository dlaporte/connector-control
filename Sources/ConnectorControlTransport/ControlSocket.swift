import Foundation
import Darwin

/// Mac only: the Windows counterpart is windows/src/ConnectorControl.Core/Transport/ControlPipe.cs.
///
/// The control layer's endpoint on the Mac: a Unix domain socket only this user can reach. The
/// socket file is mode 0600 in a folder of mode 0700; the server also asks the kernel who connected
/// (`getpeereid`) and closes anyone else's connection unread, and the client checks the server's
/// owner the same way, so nothing another account made can stand in for the app.
public enum ControlSocketError: Error, Equatable {
    /// The path does not fit a socket address: 103 bytes on macOS, its terminating NUL apart.
    case pathTooLong(String)
    /// Something answers at the path: another copy of the app is serving.
    case inUse(String)
    /// Something that is not a socket is at the path, and is left alone.
    case notASocket(String)
    /// The server at the path belongs to another user.
    case notOwner(String)
    /// The socket's folder is a link, or is not this user's: a socket there would be reachable through
    /// what someone else controls.
    case folderNotOwned(String)
    case system(String, Int32)
}

enum ControlSocket {
    /// The most a socket path may hold, its terminating NUL apart.
    static let maxPathBytes = MemoryLayout.size(ofValue: sockaddr_un().sun_path) - 1

    static func address(_ path: String) throws -> sockaddr_un {
        let bytes = Array(path.utf8)
        guard bytes.count <= maxPathBytes else { throw ControlSocketError.pathTooLong(path) }
        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        address.sun_len = UInt8(MemoryLayout<sockaddr_un>.size)
        withUnsafeMutableBytes(of: &address.sun_path) { raw in
            raw.copyBytes(from: bytes)
            raw[bytes.count] = 0
        }
        return address
    }

    static func makeSocket() throws -> Int32 {
        let fd = Darwin.socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { throw ControlSocketError.system("socket", errno) }
        noSIGPIPE(fd)
        return fd
    }

    /// A write to a peer that has gone answers EPIPE instead of ending the process.
    static func noSIGPIPE(_ fd: Int32) {
        var on: Int32 = 1
        _ = setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
    }
}

/// The app's listening socket.
public final class ControlSocketListener: @unchecked Sendable {
    public let path: String
    private let fd: Int32
    /// The socket file this listener made, so closing never removes one another made since.
    private let inode: ino_t
    private let lock = NSLock()
    private var source: DispatchSourceRead?
    private var closed = false

    private init(path: String, fd: Int32, inode: ino_t) {
        self.path = path
        self.fd = fd
        self.inode = inode
    }

    /// Tries at an existing socket file before a refusal counts as nobody there, and the pause between.
    static let staleProbes = 5
    static let staleProbePause: useconds_t = 50_000
    /// How long accepting pauses when the process is out of descriptors.
    static let descriptorPause: TimeInterval = 1

    /// What answers at an existing socket file.
    enum Probe: Equatable {
        /// Something listens: another copy of the app.
        case live
        /// Nothing does: the file a crashed app left.
        case stale
        /// A failure that says neither — permission refused, say — which leaves the file alone.
        case unknown(Int32)
    }

    /// Listens at `path`. The path is checked before anything is made. The folder is made if it is
    /// missing; it must be a real folder of this user's, not a link, and is then tightened to 0700. A
    /// socket nothing answers on — what a crashed app leaves — is removed; one something answers on is
    /// left to it, and so is one that fails for any other reason.
    public static func listen(at path: String) throws -> ControlSocketListener {
        var address = try ControlSocket.address(path)
        let folder = (path as NSString).deletingLastPathComponent
        try FileManager.default.createDirectory(atPath: folder, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        var folderInfo = stat()
        guard lstat(folder, &folderInfo) == 0, folderInfo.st_mode & S_IFMT == S_IFDIR, folderInfo.st_uid == getuid() else {
            throw ControlSocketError.folderNotOwned(folder)
        }
        guard chmod(folder, 0o700) == 0 else { throw ControlSocketError.system("chmod", errno) }
        var existing = stat()
        if lstat(path, &existing) == 0 {
            guard existing.st_mode & S_IFMT == S_IFSOCK else { throw ControlSocketError.notASocket(path) }
            switch probe(path) {
            case .live: throw ControlSocketError.inUse(path)
            case .stale: unlink(path)
            case .unknown(let code): throw ControlSocketError.system("connect", code)
            }
        }
        let fd = try ControlSocket.makeSocket()
        var bound = false
        do {
            let result = withUnsafePointer(to: &address) { pointer in
                pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
                }
            }
            guard result == 0 else { throw ControlSocketError.system("bind", errno) }
            bound = true
            // Owner-only before anyone can connect: nothing is accepted until listen().
            guard chmod(path, 0o600) == 0 else { throw ControlSocketError.system("chmod", errno) }
            guard Darwin.listen(fd, SOMAXCONN) == 0 else { throw ControlSocketError.system("listen", errno) }
            var made = stat()
            guard lstat(path, &made) == 0 else { throw ControlSocketError.system("lstat", errno) }
            return ControlSocketListener(path: path, fd: fd, inode: made.st_ino)
        } catch {
            Darwin.close(fd)
            if bound { unlink(path) }
            throw error
        }
    }

    /// Whether a socket file is live, stale, or neither. A listener whose backlog is full refuses as a
    /// dead one does on macOS, so a refusal is tried again a few times, a moment apart, before it counts
    /// as nobody there.
    static func probe(_ path: String) -> Probe {
        for attempt in 1...staleProbes {
            do {
                try ControlSocketStream.connect(to: path, owner: nil).close()
                return .live
            } catch ControlSocketError.system(_, let code) where code == ENOENT {
                return .stale
            } catch ControlSocketError.system(_, let code) where code == ECONNREFUSED {
                if attempt == staleProbes { return .stale }
                usleep(staleProbePause)
            } catch ControlSocketError.system(_, let code) {
                return .unknown(code)
            } catch {
                return .unknown(EINVAL)
            }
        }
        return .stale
    }

    /// Hands each new connection to `accepted`, on `queue`, until `close()`.
    public func start(queue: DispatchQueue, accepted: @escaping @Sendable (ControlSocketStream) -> Void) {
        _ = fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK)
        let source = DispatchSource.makeReadSource(fileDescriptor: fd, queue: queue)
        source.setEventHandler { [fd] in
            while true {
                let client = Darwin.accept(fd, nil, nil)
                guard client >= 0 else {
                    // Out of descriptors, the listener stays readable and this handler would spin:
                    // accepting pauses instead, and the waiting client is taken once it resumes.
                    if errno == EMFILE || errno == ENFILE {
                        source.suspend()
                        queue.asyncAfter(deadline: .now() + ControlSocketListener.descriptorPause) { source.resume() }
                    }
                    return
                }
                // A socket accepted from a non-blocking listener is non-blocking too on macOS; its
                // reader blocks, so it is made blocking again.
                _ = fcntl(client, F_SETFL, fcntl(client, F_GETFL) & ~O_NONBLOCK)
                ControlSocket.noSIGPIPE(client)
                accepted(ControlSocketStream(fd: client))
            }
        }
        source.setCancelHandler { [fd] in Darwin.close(fd) }
        lock.withLock { self.source = source }
        source.resume()
    }

    /// Stops listening and removes the socket file — if it is still the one this listener made — so
    /// the next start finds nothing stale.
    public func close() {
        let source: DispatchSourceRead? = lock.withLock {
            guard !closed else { return nil }
            closed = true
            var current = stat()
            if lstat(path, &current) == 0, current.st_ino == inode { unlink(path) }
            if self.source == nil { Darwin.close(fd) }
            return self.source
        }
        source?.cancel()
    }
}

/// One connection, at either end.
public final class ControlSocketStream: ControlStream, @unchecked Sendable {
    let fd: Int32
    private let lock = NSLock()
    private var shut = false

    init(fd: Int32) {
        self.fd = fd
    }

    /// The descriptor is closed only once nothing holds the stream, so a reader still blocked on it
    /// never reads a descriptor something else has since been given.
    deinit {
        Darwin.close(fd)
    }

    /// Connects to the server at `path`. With `owner`, refuses a server another user runs.
    public static func connect(to path: String, owner: uid_t? = getuid()) throws -> ControlSocketStream {
        var address = try ControlSocket.address(path)
        let stream = ControlSocketStream(fd: try ControlSocket.makeSocket())
        let result = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(stream.fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard result == 0 else { throw ControlSocketError.system("connect", errno) }
        if let owner, stream.peerUID != owner { throw ControlSocketError.notOwner(path) }
        return stream
    }

    /// The user at the other end, as the kernel says.
    public var peerUID: uid_t? {
        var uid = uid_t()
        var gid = gid_t()
        return getpeereid(fd, &uid, &gid) == 0 ? uid : nil
    }

    public func read(maximum: Int) -> Data {
        var buffer = [UInt8](repeating: 0, count: maximum)
        while true {
            let count = Darwin.read(fd, &buffer, maximum)
            if count > 0 { return Data(buffer[0..<count]) }
            if count < 0, errno == EINTR { continue }
            return Data()
        }
    }

    public func write(_ data: Data) -> Bool {
        data.withUnsafeBytes { raw -> Bool in
            guard let base = raw.baseAddress else { return true }
            var offset = 0
            while offset < raw.count {
                let written = Darwin.write(fd, base + offset, raw.count - offset)
                if written > 0 {
                    offset += written
                } else if written < 0, errno == EINTR {
                    continue
                } else {
                    return false
                }
            }
            return true
        }
    }

    /// How long a write may wait for the other end to read before it fails.
    public func setWriteTimeout(seconds: Int) {
        var limit = timeval(tv_sec: seconds, tv_usec: 0)
        _ = setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &limit, socklen_t(MemoryLayout<timeval>.size))
    }

    public func close() {
        lock.withLock {
            guard !shut else { return }
            shut = true
            Darwin.shutdown(fd, SHUT_RDWR)
        }
    }
}

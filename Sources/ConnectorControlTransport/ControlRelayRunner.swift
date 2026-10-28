import Foundation
import ConnectorControlCore

/// Mirror: windows/src/ConnectorControl.Core/Transport/ControlRelayRunner.cs
///
/// Runs `ControlRelay` between Claude — the relay's standard input and output — and the app. A thread
/// reads each side; everything the relay decides happens on one serial queue, in the order it
/// arrived, connecting included, so what Claude sends while the app starts waits its turn. Platform
/// idiom: threads and a dispatch queue here; tasks and a blocking collection on Windows. A reader here
/// cannot throw — a failure reads as the end of its stream — so its last word to the relay always
/// comes; the Windows readers, which can throw, end in a `finally` to make it so.
public final class ControlRelayRunner: @unchecked Sendable {
    private let client: ControlClient
    private let version: String
    private let input: Int32
    private let output: Int32
    private let queue = DispatchQueue(label: "com.dlaporte.connector-control.relay")
    private let relay = ControlRelay()
    private let ended = DispatchSemaphore(value: 0)
    private var stream: ControlStream?
    /// Which connection a reader belongs to: what a reader of a connection already given up reads is
    /// ignored.
    private var generation = 0
    private var exitCode: Int32?

    public init(client: ControlClient, version: String, input: Int32 = STDIN_FILENO, output: Int32 = STDOUT_FILENO) {
        self.client = client
        self.version = version
        self.input = input
        self.output = output
    }

    /// Whether the relay holds a connection to the app now. Platform idiom: asked on the relay's queue,
    /// so it waits while a connection is being made; Windows reads a copy the relay's thread keeps.
    public var isConnected: Bool { queue.sync { relay.isConnected } }

    /// Relays until Claude closes its side or the relay must end, and returns the exit code.
    public func run() -> Int32 {
        Thread.detachNewThread { [self] in readClaude() }
        ended.wait()
        return queue.sync { exitCode ?? ControlOutput.doneExit }
    }

    private func readClaude() {
        var framer = McpLineFramer()
        var buffer = [UInt8](repeating: 0, count: 65_536)
        while true {
            let count = Darwin.read(input, &buffer, buffer.count)
            if count < 0, errno == EINTR { continue }
            if count <= 0 { break }
            for frame in framer.append(Data(buffer[0..<count])) { queue.async { self.fromClaude(frame) } }
        }
        for frame in framer.finish() { queue.async { self.fromClaude(frame) } }
        queue.async { self.perform(self.relay.claudeClosed()) }
    }

    /// What Claude sent that the app would refuse unread is refused here in the app's words: the relay
    /// cannot forward a line it did not keep.
    private func fromClaude(_ frame: McpFrame) {
        guard exitCode == nil else { return }
        switch frame {
        case .line(let bytes):
            guard let line = McpSession.strictUTF8(bytes) else {
                toClaude(McpSession.errorLine(code: McpSession.parseErrorCode, message: McpSession.parseErrorMessage, id: .null))
                return
            }
            perform(relay.fromClaude(line))
        case .tooLarge:
            toClaude(McpSession.errorLine(code: McpSession.invalidRequestCode, message: McpSession.tooLargeMessage, id: .null))
        }
    }

    private func perform(_ actions: [ControlRelay.Action]) {
        for action in actions {
            guard exitCode == nil else { return }
            switch action {
            case .toApp(let line):
                // A write that fails gives the connection up; the actions after it in this batch find no
                // connection and give up nothing more, since the relay answers a lost connection once.
                if stream?.write(Data((line + "\n").utf8)) != true { lose() }
            case .toClaude(let line):
                toClaude(line)
            case .connect:
                do {
                    attach(try client.connect())
                    perform(relay.connectSucceeded())
                } catch {
                    // Any other failure leaves the app unreachable too.
                    perform(relay.connectFailed((error as? ControlClient.Failure)?.message ?? ControlClient.unreachableError))
                }
            case .exit(let code):
                finish(code)
            }
        }
    }

    private func attach(_ connection: ControlStream) {
        generation += 1
        let mine = generation
        stream = connection
        _ = connection.write(Data((ControlHello.line(client: ControlHello.relayClient, version: version) + "\n").utf8))
        Thread.detachNewThread { [self] in
            // The app's answers may be far longer than what Claude may send: a whole configuration, say.
            var framer = McpLineFramer(limit: ControlClient.maxAnswerBytes)
            while true {
                let chunk = connection.read(maximum: 65_536)
                if chunk.isEmpty { break }
                for frame in framer.append(chunk) {
                    queue.async {
                        guard mine == self.generation, self.exitCode == nil else { return }
                        if case .line(let bytes) = frame {
                            self.perform(self.relay.fromApp(String(decoding: bytes, as: UTF8.self)))
                        } else {
                            self.lose()
                        }
                    }
                }
            }
            queue.async { if mine == self.generation { self.lose() } }
        }
    }

    /// The connection has gone, or is given up: the relay answers what it was waiting for.
    private func lose() {
        stream?.close()
        stream = nil
        generation += 1
        perform(relay.connectionLost())
    }

    /// A line for Claude. When Claude's side has gone there is nobody left to relay for.
    private func toClaude(_ line: String) {
        let data = Data((line + "\n").utf8)
        let written = data.withUnsafeBytes { raw -> Bool in
            guard let base = raw.baseAddress else { return true }
            var offset = 0
            while offset < raw.count {
                let count = Darwin.write(output, base + offset, raw.count - offset)
                if count > 0 {
                    offset += count
                } else if count < 0, errno == EINTR {
                    continue
                } else {
                    return false
                }
            }
            return true
        }
        if !written { finish(ControlOutput.doneExit) }
    }

    private func finish(_ code: Int32) {
        guard exitCode == nil else { return }
        exitCode = code
        stream?.close()
        stream = nil
        ended.signal()
    }
}

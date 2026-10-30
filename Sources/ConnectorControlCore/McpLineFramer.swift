import Foundation

/// Mirror: windows/src/ConnectorControl.Core/McpLineFramer.cs
///
/// One message as the transport read it: a line's bytes without their newline, or word that a line
/// ran past the limit - whose bytes were dropped as they came, so a client cannot make the reader
/// hold more than the limit. The Windows mirror has no enum payloads, so its frames are records.
public enum McpFrame: Equatable, Sendable {
    case line(Data)
    case tooLarge
}

/// Splits a byte stream into newline-delimited messages, holding at most `limit` bytes of a line. A
/// line longer than that is reported once, as `.tooLarge`, and skipped up to its newline; the
/// stream carries on after it. Platform idiom: a struct its one owner mutates in place, where the
/// Windows mirror is a class.
public struct McpLineFramer: Sendable {
    public let limit: Int
    private var buffer = Data()
    /// Inside a line already reported as too large.
    private var skipping = false

    public init(limit: Int = McpSession.maxMessageBytes) {
        self.limit = limit
    }

    public mutating func append(_ chunk: Data) -> [McpFrame] {
        var frames: [McpFrame] = []
        var rest = chunk[chunk.startIndex...]
        while let newline = rest.firstIndex(of: 0x0A) {
            let piece = rest[rest.startIndex..<newline]
            rest = rest[rest.index(after: newline)...]
            if skipping {
                skipping = false
            } else if buffer.count + piece.count > limit {
                frames.append(.tooLarge)
            } else {
                buffer.append(contentsOf: piece)
                frames.append(.line(buffer))
            }
            buffer = Data()
        }
        if !skipping {
            if buffer.count + rest.count > limit {
                buffer = Data()
                skipping = true
                frames.append(.tooLarge)
            } else {
                buffer.append(contentsOf: rest)
            }
        }
        return frames
    }

    /// The end of the stream: a last line without its newline is still a line.
    public mutating func finish() -> [McpFrame] {
        defer {
            buffer = Data()
            skipping = false
        }
        return skipping || buffer.isEmpty ? [] : [.line(buffer)]
    }
}

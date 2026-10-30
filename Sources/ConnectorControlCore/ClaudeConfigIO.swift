import Foundation

public enum ClaudeConfigError: Error, Equatable {
    case malformed(String)
}

public enum ClaudeConfigIO {
    /// Why a config file is refused when it names one key twice; it reads inside the backup refusal's
    /// parenthesis as well as on its own.
    public static let repeatedKeyError = "the same name appears more than once"

    /// Why a config file is refused when it does not parse: fixed words, alike on both platforms and in every
    /// language, that quote nothing from the file. It reads inside the backup refusal's parenthesis as well.
    public static let notJSONError = "the text does not parse as JSON"

    public static func readMCPServers(at url: URL) throws -> [String: JSONValue] {
        guard FileManager.default.fileExists(atPath: url.path) else { return [:] }
        return try mcpServers(in: Data(contentsOf: url))
    }

    /// The servers a config file's bytes hold, parsed as `readMCPServers(at:)` parses the file, for a
    /// caller that has already read them and must use exactly what it read.
    public static func mcpServers(in data: Data) throws -> [String: JSONValue] {
        let root = try parseRoot(data)
        guard let raw = root["mcpServers"] else { return [:] }
        guard let dict = raw as? [String: Any] else {
            throw ClaudeConfigError.malformed("mcpServers is not a JSON object")
        }
        return dict.mapValues(JSONValue.init(any:))
    }

    /// Reads the file fresh, replaces ONLY the mcpServers key, preserves every
    /// other key by value, and writes atomically. Missing file → created.
    /// Malformed file → throws; the file is never overwritten blindly.
    public static func write(mcpServers: [String: JSONValue], to url: URL, staging: URL? = nil) throws {
        var root = try readRootIfPresent(at: url) ?? [:]
        root["mcpServers"] = mcpServers.mapValues(\.anyValue)
        try AtomicFile.write(try serialized(root), to: url, staging: staging)
    }

    /// The bytes this app writes for a config's top-level object, whether it changed the servers or
    /// is putting a backup back: one format, so a written file never depends on which did it.
    public static func serialized(_ root: [String: Any]) throws -> Data {
        try JSONSerialization.data(withJSONObject: root, options: [.prettyPrinted, .sortedKeys])
    }

    /// Refuses a config whose top level, or whose mcpServers object, names one key twice — exactly, or
    /// in two spellings Unicode counts as the same text, such as an é composed and decomposed. This
    /// app's parser keeps one of them and Claude's may keep the other, so a file put back as it stands
    /// could run a server the question never listed. It reads the raw bytes, since a parser has
    /// already dropped the repeat. A repeat any deeper is written back as it was parsed, so what
    /// Claude reads there is what this app read. `data` is bytes `parseRoot` has accepted.
    ///
    /// Mirror: windows/src/ConnectorControl.Core/ClaudeConfigIO.cs
    public static func requireUniqueKeys(in data: Data) throws {
        var bytes = [UInt8](data)
        // JSONSerialization accepts a byte-order mark, so parseRoot has let one through.
        if bytes.starts(with: [0xEF, 0xBB, 0xBF]) { bytes.removeFirst(3) }
        // Platform idiom: JSONSerialization keeps no repeat to look at, so this scans the bytes itself;
        // the Windows mirror's reader sees every property name.
        var open: [UInt8] = []            // the containers the scan is inside, by opening byte
        var expectingKey = false          // inside an object, where the next string is a key
        var serversNext = false           // the root's mcpServers key was just read; its value comes next
        var inServers = false             // inside the root's mcpServers object
        var rootKeys = Set<String>()
        var serverKeys = Set<String>()
        var index = 0
        while index < bytes.count {
            let byte = bytes[index]
            switch byte {
            case UInt8(ascii: "\""):
                let start = index
                index += 1
                while index < bytes.count, bytes[index] != UInt8(ascii: "\"") {
                    index += bytes[index] == UInt8(ascii: "\\") ? 2 : 1
                }
                guard expectingKey, open.last == UInt8(ascii: "{") else {
                    serversNext = false
                    break
                }
                expectingKey = false
                guard open.count == 1 || (open.count == 2 && inServers) else { break }
                // The parser's own decoding, so an escaped spelling is the text it stands for.
                let raw = Data(bytes[start...min(index, bytes.count - 1)])
                // A key the parser cannot read back is no file parseRoot accepted: refused, never skipped.
                guard let key = (try? JSONSerialization.jsonObject(with: raw, options: .fragmentsAllowed)) as? String else {
                    throw ClaudeConfigError.malformed(notJSONError)
                }
                // Platform idiom: Swift compares strings by canonical equivalence, so a set holds one of
                // each spelling; the Windows mirror composes each key first.
                if open.count == 1 {
                    guard rootKeys.insert(key).inserted else { throw ClaudeConfigError.malformed(repeatedKeyError) }
                    serversNext = key == "mcpServers"
                } else {
                    guard serverKeys.insert(key).inserted else { throw ClaudeConfigError.malformed(repeatedKeyError) }
                }
            case UInt8(ascii: "{"):
                inServers = inServers || (serversNext && open.count == 1)
                serversNext = false
                open.append(byte)
                expectingKey = true
            case UInt8(ascii: "["):
                serversNext = false
                open.append(byte)
                expectingKey = false
            case UInt8(ascii: "}"), UInt8(ascii: "]"):
                // A close with nothing open is no file parseRoot accepted; refused, so the scan can never trap.
                guard !open.isEmpty else { throw ClaudeConfigError.malformed(notJSONError) }
                open.removeLast()
                if open.count == 1 { inServers = false }
                expectingKey = false
            case UInt8(ascii: ","):
                expectingKey = open.last == UInt8(ascii: "{")
            case UInt8(ascii: ":"), UInt8(ascii: " "), UInt8(ascii: "\t"), UInt8(ascii: "\n"), UInt8(ascii: "\r"):
                break
            default:
                // A number, true, false or null: a value, so not the servers' object.
                serversNext = false
            }
            index += 1
        }
    }

    /// A zero-byte payload (crash/truncation artifact) is deliberately treated
    /// like a missing file, not malformed JSON: there is nothing in it to
    /// preserve, and callers back up before writing. A non-object top level is
    /// the one shape this app can never work with, so that alone throws.
    static func parseRoot(_ data: Data) throws -> [String: Any] {
        guard !data.isEmpty else { return [:] }
        // Platform idiom: JSONSerialization also reads UTF-16 and UTF-32, which the Windows reader, UTF-8
        // only, refuses; refused here alike, and before `requireUniqueKeys` would read their bytes as
        // UTF-8. Either one's byte-order mark, or a zero byte among the first four, which UTF-8 JSON
        // never has, marks one.
        let head = [UInt8](data.prefix(4))
        if head.starts(with: [0xFE, 0xFF]) || head.starts(with: [0xFF, 0xFE]) || head.contains(0) {
            throw ClaudeConfigError.malformed(notJSONError)
        }
        // Platform idiom: the Windows reader refuses arrays and objects nested past 64, where
        // JSONSerialization reads deeper; refused here alike, as JSONValue.parse refuses them.
        let text = String(decoding: data, as: UTF8.self)
        guard McpSession.nests(atMost: McpSession.maxNestingDepth, text) else {
            throw ClaudeConfigError.malformed(notJSONError)
        }
        // A trailing comma, which JSONSerialization reads and Claude Desktop does not, is refused as on Windows.
        guard !McpSession.hasTrailingComma(text) else { throw ClaudeConfigError.malformed(notJSONError) }
        let parsed: Any
        do {
            parsed = try JSONSerialization.jsonObject(with: data)
        } catch {
            throw ClaudeConfigError.malformed(notJSONError)
        }
        guard let root = parsed as? [String: Any] else {
            throw ClaudeConfigError.malformed("top level is not a JSON object")
        }
        return root
    }

    private static func readRootIfPresent(at url: URL) throws -> [String: Any]? {
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        // Reads yield no servers when the file is empty, which surfaces the
        // missing-MCPs recovery UI instead of a hard error.
        return try parseRoot(try Data(contentsOf: url))
    }
}

/// `localizedDescription` is the detail, not Foundation's generic "The
/// operation couldn’t be completed": the restore sheet shows it verbatim,
/// and the Windows port shows the same detail as `Message`.
extension ClaudeConfigError: LocalizedError {
    public var errorDescription: String? {
        switch self {
        case .malformed(let detail): return detail
        }
    }
}

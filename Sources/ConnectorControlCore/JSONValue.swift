import Foundation

public enum JSONValue: Hashable, Sendable {
    case null
    case bool(Bool)
    case int(Int)
    case double(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])
}

extension JSONValue: Equatable {
    // Platform idiom: text is compared scalar by scalar, as the Windows mirror compares it ordinally.
    // Swift's own == holds canonically equivalent text equal (é as one character, and as e and a
    // combining accent), which would leave a change to how a character is composed unwritten to
    // Claude's config: the bytes differ, so the config does. Object keys are compared the same way.
    /// Whether two values are the same, value for value and scalar for scalar.
    public static func == (one: JSONValue, other: JSONValue) -> Bool {
        switch (one, other) {
        case (.null, .null): return true
        case (.bool(let a), .bool(let b)): return a == b
        case (.int(let a), .int(let b)): return a == b
        case (.double(let a), .double(let b)): return a == b
        case (.string(let a), .string(let b)): return a.hasSameScalars(as: b)
        case (.array(let a), .array(let b)): return a.count == b.count && zip(a, b).allSatisfy { $0 == $1 }
        case (.object(let a), .object(let b)): return identical(a, b)
        default: return false
        }
    }

    /// Two objects with the same keys, spelled scalar for scalar alike, and the same value under each.
    public static func identical(_ one: [String: JSONValue], _ other: [String: JSONValue]) -> Bool {
        guard one.count == other.count else { return false }
        let keys = one.keys.sorted { $0.ordinallyPrecedes($1) }
        let otherKeys = other.keys.sorted { $0.ordinallyPrecedes($1) }
        return zip(keys, otherKeys).allSatisfy { key, otherKey in
            key.hasSameScalars(as: otherKey) && one[key]! == other[otherKey]!
        }
    }

    // The synthesized hash stands: values equal scalar for scalar are canonically equal too, and so
    // hash alike, which is all Hashable asks.
}

extension JSONValue: Codable {
    public init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() { self = .null }
        else if let b = try? c.decode(Bool.self) { self = .bool(b) }
        else if let i = try? c.decode(Int.self) { self = .int(i) }
        else if let d = try? c.decode(Double.self) { self = .double(d) }
        else if let s = try? c.decode(String.self) { self = .string(s) }
        else if let a = try? c.decode([JSONValue].self) { self = .array(a) }
        else if let o = try? c.decode([String: JSONValue].self) { self = .object(o) }
        else {
            throw DecodingError.dataCorruptedError(
                in: c, debugDescription: "Unsupported JSON value")
        }
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch self {
        case .null: try c.encodeNil()
        case .bool(let b): try c.encode(b)
        case .int(let i): try c.encode(i)
        case .double(let d): try c.encode(d)
        case .string(let s): try c.encode(s)
        case .array(let a): try c.encode(a)
        case .object(let o): try c.encode(o)
        }
    }
}

public extension JSONValue {
    /// How deeply a document may nest arrays and objects; the Windows parser's own limit, set on both
    /// sides so neither accepts what the other refuses.
    static let maxNestingDepth = 64

    /// Whether `text` nests arrays and objects no deeper than `limit`, brackets inside strings not counted.
    static func nests(atMost limit: Int, _ text: String) -> Bool {
        var depth = 0
        var inString = false
        var escaped = false
        for byte in text.utf8 {
            if inString {
                if escaped { escaped = false } else if byte == UInt8(ascii: "\\") { escaped = true } else if byte == UInt8(ascii: "\"") { inString = false }
            } else if byte == UInt8(ascii: "\"") {
                inString = true
            } else if byte == UInt8(ascii: "[") || byte == UInt8(ascii: "{") {
                depth += 1
                if depth > limit { return false }
            } else if byte == UInt8(ascii: "]") || byte == UInt8(ascii: "}") {
                depth -= 1
            }
        }
        return true
    }

    /// Whether `text` has a comma, outside a string, followed by nothing but JSON whitespace and then a
    /// closing bracket or brace. Platform idiom: `JSONDecoder` and `JSONSerialization` read one trailing
    /// comma, where the Windows reader - and Claude Desktop - refuse it; refused here alike, so a line or a
    /// file reads the same on both.
    static func hasTrailingComma(_ text: String) -> Bool {
        var inString = false
        var escaped = false
        var comma = false
        for byte in text.utf8 {
            if inString {
                if escaped { escaped = false } else if byte == UInt8(ascii: "\\") { escaped = true } else if byte == UInt8(ascii: "\"") { inString = false }
            } else if byte == UInt8(ascii: ",") {
                comma = true
            } else if byte == UInt8(ascii: "]") || byte == UInt8(ascii: "}") {
                if comma { return true }
            } else if byte != UInt8(ascii: " "), byte != UInt8(ascii: "\t"), byte != UInt8(ascii: "\n"), byte != UInt8(ascii: "\r") {
                comma = false
                if byte == UInt8(ascii: "\"") { inString = true }
            }
        }
        return false
    }

    /// Whether the Windows reader, UTF-8 only, refuses `data` where Foundation's readers take it: UTF-16
    /// or UTF-32, nesting past `maxNestingDepth`, or a trailing comma (`hasTrailingComma`). Claude's config
    /// and the master list are both refused for it, so each reads alike on both platforms. A UTF-8
    /// byte-order mark is read on both.
    static func isRefusedByTheWindowsReader(_ data: Data) -> Bool {
        // Either one's byte-order mark, or a zero byte among the first four, which UTF-8 JSON never has,
        // marks UTF-16 or UTF-32; checked first, before the scans below would read their bytes as UTF-8.
        let head = [UInt8](data.prefix(4))
        if head.starts(with: [0xFE, 0xFF]) || head.starts(with: [0xFF, 0xFE]) || head.contains(0) { return true }
        let text = String(decoding: data, as: UTF8.self)
        return !nests(atMost: maxNestingDepth, text) || hasTrailingComma(text)
    }

    /// Strict parse, refusing arrays and objects nested deeper than `maxNestingDepth` and a
    /// trailing comma (`hasTrailingComma`).
    static func parse(_ data: Data) throws -> JSONValue {
        // Platform idiom: the Windows reader refuses nesting past 64 itself, where JSONDecoder reads
        // to 512; held to the same limit here, so a document reads, and is masked, alike on both.
        let text = String(decoding: data, as: UTF8.self)
        guard nests(atMost: maxNestingDepth, text) else {
            throw DecodingError.dataCorrupted(.init(codingPath: [], debugDescription: "nested too deeply"))
        }
        guard !hasTrailingComma(text) else {
            throw DecodingError.dataCorrupted(.init(codingPath: [], debugDescription: "trailing comma"))
        }
        return try JSONDecoder().decode(JSONValue.self, from: data)
    }

    func serialized() throws -> Data {
        try JSONEncoder.canonical.encode(self)
    }

    /// Pretty JSON for display/editing - like `serialized()` but without
    /// escaping forward slashes, so URLs read cleanly.
    func editorText() -> String {
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        return (try? e.encode(self)).map { String(decoding: $0, as: UTF8.self) } ?? "{}"
    }

    /// One line of compact, key-sorted JSON: the control layer's wire format. JSONSerialization
    /// writes it, which the Windows mirror reproduces byte for byte
    /// (`AppleJsonFormat.SerializationCompact`, pinned by the serialization-compact goldens).
    ///
    /// `escapingSlashes: false` is for what the command line prints under `--json`, which people and
    /// scripts read: a path comes out as written. The wire is always escaped.
    func compactLine(escapingSlashes: Bool = true) -> String {
        // JSONSerialization raises an Objective-C exception, which aborts the process, for NaN and
        // infinity, so a value it cannot write is checked for first. Wrapped in an array so a bare
        // fragment is checked too.
        guard JSONSerialization.isValidJSONObject([anyValue]),
              let data = try? JSONSerialization.data(withJSONObject: anyValue,
                                                   options: escapingSlashes ? [.sortedKeys, .fragmentsAllowed]
                                                       : [.sortedKeys, .fragmentsAllowed, .withoutEscapingSlashes]) else {
            return "null"
        }
        return String(decoding: data, as: UTF8.self)
    }

    var anyValue: Any {
        switch self {
        case .null: return NSNull()
        case .bool(let b): return b
        case .int(let i): return i
        case .double(let d): return d
        case .string(let s): return s
        case .array(let a): return a.map(\.anyValue)
        case .object(let o): return o.mapValues(\.anyValue)
        }
    }

    init(any: Any) {
        switch any {
        case is NSNull:
            self = .null
        case let n as NSNumber:
            if CFGetTypeID(n) == CFBooleanGetTypeID() { self = .bool(n.boolValue) }
            else if CFNumberIsFloatType(n) {
                let d = n.doubleValue
                // Canonicalize whole-valued floats (2.0 → 2): the write path
                // re-emits them as integers anyway, and a stable representation
                // prevents reconcile/backup churn on every reload.
                if d.rounded() == d, let i = Int(exactly: d) { self = .int(i) }
                else { self = .double(d) }
            }
            else { self = .int(n.intValue) }
        case let s as String:
            self = .string(s)
        case let a as [Any]:
            self = .array(a.map(JSONValue.init(any:)))
        case let o as [String: Any]:
            self = .object(o.mapValues(JSONValue.init(any:)))
        default:
            preconditionFailure("JSONSerialization produced \(type(of: any))")
        }
    }

    var typeName: String {
        switch self {
        case .null: return "null"
        case .bool: return "boolean"
        case .int, .double: return "number"
        case .string: return "string"
        case .array: return "array"
        case .object: return "object"
        }
    }
}

extension JSONEncoder {
    /// Pretty, key-sorted output - the on-disk file format everywhere this app
    /// writes JSON (`JSONValue.serialized()`, `MasterStoreIO.save`). A fresh
    /// instance per use: `JSONEncoder` is a class, and nothing here needs to
    /// share one across calls.
    static var canonical: JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return encoder
    }
}

/// A path into a JSONValue, written and parsed in RFC 6901 form ("/args/1", "~1" for a slash,
/// "~0" for a tilde). Segments are kept as strings; an array decides at lookup time whether a
/// segment is an index, so an object key that happens to be digits still resolves.
public struct JSONPointer: Equatable, Hashable, Sendable, CustomStringConvertible {
    public var segments: [String]

    public init(_ segments: [String]) { self.segments = segments }

    public init?(string: String) {
        if string.isEmpty { segments = []; return }
        guard string.hasPrefix("/") else { return nil }
        segments = string.dropFirst().split(separator: "/", omittingEmptySubsequences: false).map {
            $0.replacingOccurrences(of: "~1", with: "/").replacingOccurrences(of: "~0", with: "~")
        }
    }

    public var description: String {
        segments.map { "/" + $0.replacingOccurrences(of: "~", with: "~0").replacingOccurrences(of: "/", with: "~1") }.joined()
    }

    public func appending(_ segment: String) -> JSONPointer { JSONPointer(segments + [segment]) }
}

public extension JSONValue {
    func value(at pointer: JSONPointer) -> JSONValue? {
        var current = self
        for segment in pointer.segments {
            switch current {
            case .object(let object):
                guard let next = object[segment] else { return nil }
                current = next
            case .array(let array):
                guard let index = Int(segment), array.indices.contains(index) else { return nil }
                current = array[index]
            default:
                return nil
            }
        }
        return current
    }

    /// A copy with the value at `pointer` replaced; nil when the path does not exist, so a caller
    /// never creates structure by accident.
    func replacing(at pointer: JSONPointer, with newValue: JSONValue) -> JSONValue? {
        guard let first = pointer.segments.first else { return newValue }
        let rest = JSONPointer(Array(pointer.segments.dropFirst()))
        switch self {
        case .object(var object):
            guard let child = object[first], let replaced = child.replacing(at: rest, with: newValue) else { return nil }
            object[first] = replaced
            return .object(object)
        case .array(var array):
            guard let index = Int(first), array.indices.contains(index),
                  let replaced = array[index].replacing(at: rest, with: newValue) else { return nil }
            array[index] = replaced
            return .array(array)
        default:
            return nil
        }
    }

    /// Every string leaf with its pointer, depth first, object keys in sorted order, so two
    /// platforms walking the same value produce the same list.
    var stringLeaves: [(pointer: JSONPointer, value: String)] {
        var out: [(pointer: JSONPointer, value: String)] = []
        func walk(_ value: JSONValue, _ pointer: JSONPointer) {
            switch value {
            case .string(let s): out.append((pointer, s))
            case .array(let items): for (i, item) in items.enumerated() { walk(item, pointer.appending(String(i))) }
            case .object(let object): for key in object.keys.sorted() { walk(object[key]!, pointer.appending(key)) }
            default: break
            }
        }
        walk(self, JSONPointer([]))
        return out
    }
}

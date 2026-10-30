/// The marker a shared collection leaves where a secret or a machine-specific path was:
/// `${CC_NEEDS:<name>}`. A string that contains one is "unfilled"; filling replaces the whole
/// string. `${COLLECTION_DIR}` is the one other token, expanded per machine to the folder the
/// collection document lives in.
public enum Placeholder {
    static let markerPrefix = "${CC_NEEDS:"
    static let markerSuffix = "}"
    public static let directoryToken = "${COLLECTION_DIR}"

    public static func marker(_ name: String) -> String { markerPrefix + name + markerSuffix }

    public static func isValidName(_ name: String) -> Bool {
        !name.isEmpty && name.unicodeScalars.allSatisfy { s in
            switch s {
            case "0"..."9", "A"..."Z", "a"..."z", "_": return true
            default: return false
            }
        }
    }

    /// Marker names in order of first appearance; a malformed marker (bad name, no closing brace)
    /// is plain text.
    public static func names(in text: String) -> [String] {
        // Platform idiom: searched scalar by scalar, as the Windows mirror searches UTF-16 units; a
        // Swift Character would carry a combining mark after the closing brace along with it, so
        // the brace would not be found and the marker would read as plain text on the Mac only.
        let scalars = Array(text.unicodeScalars)
        let prefix = Array(markerPrefix.unicodeScalars)
        var out: [String] = []
        var index = 0
        while let start = firstIndex(of: prefix, in: scalars, from: index) {
            let nameStart = start + prefix.count
            guard let close = scalars[nameStart...].firstIndex(of: "}") else { break }
            let name = String(String.UnicodeScalarView(scalars[nameStart..<close]))
            if isValidName(name), !out.contains(name) { out.append(name) }
            index = close + 1
        }
        return out
    }

    /// Where `needle` first starts in `scalars` at or after `from`, or nil.
    private static func firstIndex(of needle: [Unicode.Scalar], in scalars: [Unicode.Scalar], from: Int) -> Int? {
        guard scalars.count - needle.count >= from else { return nil }
        return (from...(scalars.count - needle.count)).first { scalars[$0..<($0 + needle.count)].elementsEqual(needle) }
    }

    public static func containsMarker(_ text: String) -> Bool { !names(in: text).isEmpty }

    public static func markers(in config: JSONValue) -> [(pointer: JSONPointer, names: [String])] {
        config.stringLeaves.compactMap { leaf in
            let found = names(in: leaf.value)
            return found.isEmpty ? nil : (leaf.pointer, found)
        }
    }

    /// Every marker name the config still asks for, ordered by first appearance and
    /// de-duplicated across leaves, so a sentence built from them is stable between two reads of
    /// the same config and the same wherever it is built.
    public static func unfilledNames(in config: JSONValue) -> [String] {
        var seen: Set<String> = []
        return markers(in: config).flatMap(\.names).filter { seen.insert($0).inserted }
    }

    public static func usesDirectoryToken(_ config: JSONValue) -> Bool {
        config.stringLeaves.contains { $0.value.contains(directoryToken) }
    }

    public static func expandDirectoryToken(in config: JSONValue, directory: String) -> JSONValue {
        var result = config
        for leaf in config.stringLeaves where leaf.value.contains(directoryToken) {
            let expanded = leaf.value.replacingOccurrences(of: directoryToken, with: directory)
            result = result.replacing(at: leaf.pointer, with: .string(expanded)) ?? result
        }
        return result
    }
}

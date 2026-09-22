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
            let v = s.value
            return (v >= 0x30 && v <= 0x39) || (v >= 0x41 && v <= 0x5A) || (v >= 0x61 && v <= 0x7A) || v == 0x5F
        }
    }

    /// Marker names in order of first appearance; a malformed marker (bad name, no closing brace)
    /// is plain text.
    public static func names(in text: String) -> [String] {
        var out: [String] = []
        var rest = Substring(text)
        while let range = rest.range(of: markerPrefix) {
            let afterPrefix = rest[range.upperBound...]
            guard let close = afterPrefix.firstIndex(of: "}") else { break }
            let name = String(afterPrefix[..<close])
            if isValidName(name), !out.contains(name) { out.append(name) }
            rest = afterPrefix[afterPrefix.index(after: close)...]
        }
        return out
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

    /// `config` with `directory` written back as `${COLLECTION_DIR}` wherever it stands as a folder
    /// of its own: not inside a longer name, and followed by a separator or the end of the string.
    /// The way back from `expandDirectoryToken` for a config that comes out of Claude's file, which
    /// holds this machine's folder where the store holds the token.
    public static func collapseDirectory(in config: JSONValue, directory: String) -> JSONValue {
        guard !directory.isEmpty else { return config }
        var result = config
        for leaf in config.stringLeaves where leaf.value.contains(directory) {
            result = result.replacing(at: leaf.pointer, with: .string(collapse(leaf.value, directory: directory))) ?? result
        }
        return result
    }

    private static func collapse(_ text: String, directory: String) -> String {
        var out = ""
        var rest = Substring(text)
        while let range = rest.range(of: directory) {
            let before = range.lowerBound == rest.startIndex ? out.last : rest[rest.index(before: range.lowerBound)]
            let after = range.upperBound == rest.endIndex ? nil : rest[range.upperBound]
            let standsAlone = (before.map { !isPathCharacter($0) } ?? true) && (after.map { $0 == "/" || $0 == "\\" } ?? true)
            out += rest[..<range.lowerBound]
            out += standsAlone ? directoryToken : String(rest[range])
            rest = rest[range.upperBound...]
        }
        return out + rest
    }

    /// A character that continues a path segment, so a folder found right after one is part of a
    /// longer name.
    private static func isPathCharacter(_ c: Character) -> Bool {
        c.isLetter || c.isNumber || "/\\._-~".contains(c)
    }
}

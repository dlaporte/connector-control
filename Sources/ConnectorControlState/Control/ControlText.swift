import ConnectorControlCore

/// Mirror: windows/src/ConnectorControl.Core/State/Control/ControlText.cs
///
/// The layout of the control layer's text results: label/value lines and tables, padded with
/// spaces so they line up in a terminal. Widths count Unicode scalars, which the Windows mirror
/// counts as runes, so the two platforms pad every line identically.
public enum ControlText {
    public static let listAnd = " and "
    /// The spaces after the widest cell of a column.
    static let gap = 3

    // Platform idiom: Unicode scalars here; a .NET string is UTF-16, so the Windows mirror counts runes.
    public static func width(_ text: String) -> Int { text.unicodeScalars.count }

    public static func pad(_ text: String, to width: Int) -> String {
        text + String(repeating: " ", count: max(0, width - ControlText.width(text)))
    }

    /// "label   value" lines, every value starting in the same column. A row with an empty label
    /// continues the one above it. Labels and values go through `escaped(_:)`.
    public static func labelled(_ rows: [(String, String)]) -> [String] {
        let rows = rows.map { (escaped($0.0), escaped($0.1)) }
        let column = (rows.map { width($0.0) }.max() ?? 0) + gap
        return rows.map { trimmingTrailingSpaces(pad($0.0, to: column) + $0.1) }
    }

    /// A table: every column but the last padded to its widest cell plus the gap; the header row,
    /// when there is one, first. No line ends in spaces, whatever its last cells hold. Every cell
    /// goes through `escaped(_:)`.
    public static func table(header: [String]?, rows: [[String]]) -> [String] {
        let all = ((header.map { [$0] } ?? []) + rows).map { $0.map(escaped) }
        let columns = all.map(\.count).max() ?? 0
        let widths = (0..<columns).map { index in
            (all.map { index < $0.count ? width($0[index]) : 0 }.max() ?? 0) + gap
        }
        return all.map { cells in
            trimmingTrailingSpaces(cells.enumerated().map { index, cell in
                index == cells.count - 1 ? cell : pad(cell, to: widths[index])
            }.joined())
        }
    }

    // Platform idiom: the spaces are stripped one at a time here; the Windows mirror's TrimEnd does it in one call.
    static func trimmingTrailingSpaces(_ line: String) -> String {
        var trimmed = line
        while trimmed.hasSuffix(" ") { trimmed.removeLast() }
        return trimmed
    }

    /// Text as a line shows it: a control character, and a format character that reorders or hides
    /// text (U+061C, U+200B–U+200F, U+2028–U+202E, U+2066–U+2069, U+FEFF), as a visible escape —
    /// `\n`, `\r`, `\t` or `\u{202E}` — so a value can neither break its line nor hide part of it.
    public static func escaped(_ text: String) -> String {
        var line = ""
        for scalar in text.unicodeScalars { line += escape(scalar) ?? String(scalar) }
        return line
    }

    // Platform idiom: one scalar at a time here, one UTF-16 unit on Windows; every character escaped
    // is in the Basic Plane, so both escape the same ones.
    /// The escape for one character, or nil for one shown as it is.
    static func escape(_ scalar: Unicode.Scalar) -> String? {
        switch scalar.value {
        case 0x0A: return "\\n"
        case 0x0D: return "\\r"
        case 0x09: return "\\t"
        case 0x00...0x1F, 0x7F...0x9F, 0x061C, 0x200B...0x200F, 0x2028...0x202E, 0x2066...0x2069, 0xFEFF:
            let hex = String(scalar.value, radix: 16, uppercase: true)
            return "\\u{" + String(repeating: "0", count: max(0, 4 - hex.count)) + hex + "}"
        default: return nil
        }
    }

    /// One argument of a command line: as written when it reads as one word — not empty, with no
    /// space, no ' and nothing `escaped(_:)` escapes — else between single quotes, with \ and '
    /// escaped inside as well, so no two argument lists read alike.
    public static func word(_ text: String) -> String {
        if !text.isEmpty, !text.unicodeScalars.contains(where: { isSpace($0) || $0 == "'" || escape($0) != nil }) { return text }
        var word = "'"
        for scalar in text.unicodeScalars {
            switch scalar {
            case "\\": word += "\\\\"
            case "'": word += "\\'"
            default: word += escape(scalar) ?? String(scalar)
            }
        }
        return word + "'"
    }

    /// An argument list on one line: each through `word(_:)`, between spaces.
    public static func words(_ args: [String]) -> String {
        args.map(word).joined(separator: " ")
    }

    /// A space, as the Windows mirror's TrimSpaces reads one: any space separator.
    static func isSpace(_ scalar: Unicode.Scalar) -> Bool {
        scalar.properties.generalCategory == .spaceSeparator
    }

    /// “a”, “a” and “b”, “a”, “b” and “c” — names as the app's sentences list them.
    public static func quotedList(_ names: [String]) -> String {
        let quoted = names.map { "“\($0)”" }
        guard let last = quoted.last else { return "" }
        guard quoted.count > 1 else { return last }
        return quoted.dropLast().joined(separator: ", ") + listAnd + last
    }
}

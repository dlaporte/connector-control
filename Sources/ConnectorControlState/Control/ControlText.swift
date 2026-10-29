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
    /// continues the one above it. Labels and values go through `escapedValue(_:)`.
    public static func labelled(_ rows: [(String, String)]) -> [String] {
        let rows = rows.map { (escapedValue($0.0), escapedValue($0.1)) }
        let column = (rows.map { width($0.0) }.max() ?? 0) + gap
        return rows.map { trimmingTrailingSpaces(pad($0.0, to: column) + $0.1) }
    }

    /// A table: every column but the last padded to its widest cell plus the gap; the header row,
    /// when there is one, first. No line ends in spaces, whatever its last cells hold. Every cell
    /// goes through `escaped(_:)`; a cell that holds a name comes in through `escapedName(_:)`.
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

    /// Text as a line shows it: a control character, and a character that shows as nothing or as a
    /// blank — every Default_Ignorable code point (soft hyphen, joiners, direction marks, variation
    /// selectors, tags…), the blank letters (U+115F, U+1160, U+2800, U+3164, U+FFA0), the interlinear
    /// annotation marks — as a visible escape, `\n`, `\r`, `\t` or `\u{202E}`, so a value
    /// can neither break its line, nor hide part of it, nor pass for another.
    public static func escaped(_ text: String) -> String {
        var line = ""
        for scalar in text.unicodeScalars { line += escape(scalar) ?? String(scalar) }
        return line
    }

    /// One of the app's own sentences that quotes names someone else chose, read once from the left: at
    /// each place, the longest of `names` whose quoted form — “, the name, ” — starts there is shown as
    /// “ + `escapedName(_:)` + ”, and anything else — the sentence's own quotes included — as `escaped(_:)`
    /// does. A name is found only between the app's own quotes, so a name that begins with “ can never
    /// match at another name's opening quote, and no name's escape is ever read again as part of another
    /// name. Names are matched scalar by scalar, as the Windows mirror matches them ordinally; equal
    /// lengths in ordinal order, so both platforms pick the same name.
    public static func escaped(_ text: String, naming names: [String]) -> String {
        let ordered = names.filter { !$0.isEmpty }.sorted { one, other in
            let a = one.unicodeScalars.count, b = other.unicodeScalars.count
            return a != b ? a > b : one.ordinallyPrecedes(other)
        }.map { name -> (name: String, quoted: [Unicode.Scalar]) in
            (name, ["\u{201C}"] + Array(name.unicodeScalars) + ["\u{201D}"])
        }
        let scalars = Array(text.unicodeScalars)
        var line = ""
        var index = 0
        while index < scalars.count {
            if let found = ordered.first(where: { index + $0.quoted.count <= scalars.count && scalars[index..<index + $0.quoted.count].elementsEqual($0.quoted) }) {
                line += "\u{201C}" + escapedName(found.name) + "\u{201D}"
                index += found.quoted.count
            } else {
                line += escape(scalars[index]) ?? String(scalars[index])
                index += 1
            }
        }
        return line
    }

    /// A name as the app's sentences quote it: as `escaped(_:)` shows it, with “ and ” escaped as
    /// well, so a name can neither break its line nor close its own quotes and read as two names, and
    /// a backslash doubled, as `word(_:)` doubles one, so no two names read alike.
    public static func escapedName(_ text: String) -> String {
        var name = ""
        for scalar in text.unicodeScalars {
            switch scalar {
            case "\\": name += "\\\\"
            case "\u{201C}", "\u{201D}": name += hexEscape(scalar)
            default: name += escapeShown(scalar) ?? String(scalar)
            }
        }
        return name
    }

    /// A key or a value on a line: as `escaped(_:)` shows it, and a space-like character as its code, since
    /// there it could pass for the ordinary space. Backslashes stay as they are: a value may be a path.
    public static func escapedValue(_ text: String) -> String {
        var value = ""
        for scalar in text.unicodeScalars { value += escapeShown(scalar) ?? String(scalar) }
        return value
    }

    /// A path as a dialog's footer quotes it: as `escaped(_:)` shows it, with “ and ” escaped as well so
    /// it cannot close the app's quotes around it, and its backslashes as they are — on Windows they are
    /// the path's own separators.
    public static func escapedPath(_ text: String) -> String {
        var path = ""
        for scalar in text.unicodeScalars {
            path += scalar == "\u{201C}" || scalar == "\u{201D}" ? hexEscape(scalar) : escapeShown(scalar) ?? String(scalar)
        }
        return path
    }

    // Platform idiom: one scalar at a time here; a .NET string is UTF-16, so the Windows mirror walks
    // the same scalars itself, and shows a lone surrogate, which only it can hold, as its code.
    /// The escape for one character, or nil for one shown as it is.
    static func escape(_ scalar: Unicode.Scalar) -> String? {
        switch scalar.value {
        case 0x0A: return "\\n"
        case 0x0D: return "\\r"
        case 0x09: return "\\t"
        case 0x00...0x1F, 0x7F...0x9F, 0xAD, 0x034F, 0x061C, 0x115F, 0x1160, 0x17B4, 0x17B5, 0x180B...0x180F,
             0x200B...0x200F, 0x2028...0x202E, 0x2060...0x206F, 0x2800, 0x3164, 0xFE00...0xFE0F, 0xFEFF, 0xFFA0,
             0xFFF0...0xFFFB, 0x1BCA0...0x1BCA3, 0x1D173...0x1D17A, 0xE0000...0xE0FFF:
            return hexEscape(scalar)
        default: return nil
        }
    }

    /// The escape for one character of a name, a key, a value or a path: as `escape(_:)`, and a space that is
    /// not the ordinary one too (U+00A0, U+1680, U+2000–U+200A, U+202F, U+205F, U+3000), which could pass for
    /// it there. Free text keeps them: a localized system message sets its punctuation off with one.
    static func escapeShown(_ scalar: Unicode.Scalar) -> String? {
        switch scalar.value {
        case 0xA0, 0x1680, 0x2000...0x200A, 0x202F, 0x205F, 0x3000: return hexEscape(scalar)
        default: return escape(scalar)
        }
    }

    /// A character by its code, as `\u{202E}`: at least four hex digits, five beyond the Basic Plane.
    static func hexEscape(_ scalar: Unicode.Scalar) -> String {
        let hex = String(scalar.value, radix: 16, uppercase: true)
        return "\\u{" + String(repeating: "0", count: max(0, 4 - hex.count)) + hex + "}"
    }

    /// One argument of a command line: as written when it reads as one word — not empty, with no
    /// space, no ' and nothing `escaped(_:)` escapes — else between single quotes, with \ and '
    /// escaped inside as well, so no two argument lists read alike.
    public static func word(_ text: String) -> String {
        if !text.isEmpty, !text.unicodeScalars.contains(where: { isSpace($0) || $0 == "'" || escapeShown($0) != nil }) { return text }
        var word = "'"
        for scalar in text.unicodeScalars {
            switch scalar {
            case "\\": word += "\\\\"
            case "'": word += "\\'"
            default: word += escapeShown(scalar) ?? String(scalar)
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

    /// “a”, “a” and “b”, “a”, “b” and “c” — names as the app's sentences list them, each through
    /// `escapedName(_:)`.
    public static func quotedList(_ names: [String]) -> String {
        let quoted = names.map { "“\(escapedName($0))”" }
        guard let last = quoted.last else { return "" }
        guard quoted.count > 1 else { return last }
        return quoted.dropLast().joined(separator: ", ") + listAnd + last
    }
}

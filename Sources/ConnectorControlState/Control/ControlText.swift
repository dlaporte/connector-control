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
    /// annotation marks, the object replacement character — as a visible escape, `\n`, `\r`, `\t` or `\u{202E}`, so a value
    /// can neither break its line, nor hide part of it, nor pass for another. A combining mark past the
    /// second on a character, or with no character to sit on, shows as its code too (see `Marks`).
    public static func escaped(_ text: String) -> String {
        var line = ""
        var marks = Marks()
        for scalar in text.unicodeScalars { line += marks.bare(scalar) ? hexEscape(scalar) : escape(scalar) ?? String(scalar) }
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
        var marks = Marks()
        while index < scalars.count {
            if let found = ordered.first(where: { index + $0.quoted.count <= scalars.count && scalars[index..<index + $0.quoted.count].elementsEqual($0.quoted) }) {
                line += "\u{201C}" + escapedName(found.name) + "\u{201D}"
                index += found.quoted.count
                // The closing quote is a character a mark after it sits on.
                marks = Marks(atStart: false)
            } else {
                line += marks.bare(scalars[index]) ? hexEscape(scalars[index]) : escape(scalars[index]) ?? String(scalars[index])
                index += 1
            }
        }
        return line
    }

    /// A name as the app's sentences quote it: as `escaped(_:)` shows it, with “ and ” escaped as
    /// well, so a name can neither break its line nor close its own quotes and read as two names, “…”
    /// escaped, so a name's own cannot pass for where a long name was cut (`TextRun.cut(_:_:)`), and
    /// a backslash doubled, as `word(_:)` doubles one, so no two names read alike.
    public static func escapedName(_ text: String) -> String {
        var name = ""
        var marks = Marks()
        for scalar in text.unicodeScalars {
            let bare = marks.bare(scalar)
            switch scalar {
            case "\\": name += "\\\\"
            case "\u{201C}", "\u{201D}", "\u{2026}": name += hexEscape(scalar)
            default: name += bare ? hexEscape(scalar) : escapeShown(scalar) ?? String(scalar)
            }
        }
        return name
    }

    /// A key or a value on a line: as `escaped(_:)` shows it, and a space-like character as its code, since
    /// there it could pass for the ordinary space. Backslashes stay as they are: a value may be a path.
    public static func escapedValue(_ text: String) -> String {
        var value = ""
        var marks = Marks()
        for scalar in text.unicodeScalars { value += marks.bare(scalar) ? hexEscape(scalar) : escapeShown(scalar) ?? String(scalar) }
        return value
    }

    /// A path as a dialog's footer quotes it: as `escaped(_:)` shows it, with “ and ” escaped as well so
    /// it cannot close the app's quotes around it, and its backslashes as they are — on Windows they are
    /// the path's own separators.
    public static func escapedPath(_ text: String) -> String {
        var path = ""
        var marks = Marks()
        for scalar in text.unicodeScalars {
            let bare = marks.bare(scalar)
            path += scalar == "\u{201C}" || scalar == "\u{201D}" || bare ? hexEscape(scalar) : escapeShown(scalar) ?? String(scalar)
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
             0xFFF0...0xFFFC, 0x1BCA0...0x1BCA3, 0x1D173...0x1D17A, 0xE0000...0xE0FFF:
            return hexEscape(scalar)
        default: return nil
        }
    }

    /// The combining marks: each is drawn over the character before it, and several on one character
    /// are drawn stacked, as high or as deep as they go, over the lines above and below. Explicit ranges,
    /// the same as the Windows mirror's, so neither platform leans on its own Unicode tables.
    static func isCombiningMark(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x0300...0x036F, 0x0483...0x0489, 0x0591...0x05BD, 0x0610...0x061A, 0x064B...0x065F, 0x0670,
             0x06D6...0x06DC, 0x06DF...0x06E4, 0x06E7...0x06E8, 0x06EA...0x06ED, 0x0E31, 0x0E34...0x0E3A,
             0x0E47...0x0E4E, 0x1AB0...0x1AFF, 0x1DC0...0x1DFF, 0x20D0...0x20FF, 0xFE20...0xFE2F:
            return true
        default: return false
        }
    }

    /// The rule for combining marks as a text is read from its start: the first two marks on a character
    /// are drawn, and a third or later one, or a mark that starts the text, shows as its code. Two, because
    /// ordinary text needs two — a Thai vowel and its tone, decomposed Vietnamese, a Hebrew dagesh and
    /// vowel, an Arabic shadda and vowel — and two stay within the line; painting over the lines around
    /// it takes a deep stack, which no character can now carry.
    struct Marks {
        /// The most marks drawn on one character.
        static let drawn = 2
        /// Whether a character has been read yet: until one has, a mark has nothing to sit on.
        private var started: Bool
        /// The marks read since the last character that is not one.
        private var stacked = 0

        /// At a text's start, where a mark has nothing before it; or, `atStart` false, just after a
        /// character that is not a mark.
        init(atStart: Bool = true) { started = !atStart }

        /// Reads `scalar`, the text's next, and says whether it is a mark that is not drawn, which shows
        /// as its code.
        mutating func bare(_ scalar: Unicode.Scalar) -> Bool {
            guard ControlText.isCombiningMark(scalar) else {
                started = true
                stacked = 0
                return false
            }
            stacked += 1
            return !started || stacked > Marks.drawn
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
    /// space, no ' and nothing `escapedValue(_:)` escapes — else between single quotes, with \ and '
    /// escaped inside as well, so no two argument lists read alike.
    public static func word(_ text: String) -> String {
        var reading = Marks()
        if !text.isEmpty, !text.unicodeScalars.contains(where: { reading.bare($0) || isSpace($0) || $0 == "'" || escapeShown($0) != nil }) {
            return text
        }
        var word = "'"
        var marks = Marks()
        for scalar in text.unicodeScalars {
            let bare = marks.bare(scalar)
            switch scalar {
            case "\\": word += "\\\\"
            case "'": word += "\\'"
            default: word += bare ? hexEscape(scalar) : escapeShown(scalar) ?? String(scalar)
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

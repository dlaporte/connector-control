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

    /// `text` with every combining mark left out: what a line's own letters need room for, apart from
    /// any marks stacked on them, which the line's box clips rather than makes room for.
    public static func withoutMarks(_ text: String) -> String {
        var view = String.UnicodeScalarView()
        view.append(contentsOf: text.unicodeScalars.filter { !isCombiningMark($0) })
        return String(view)
    }

    /// The combining marks: each is drawn over, under or around the character before it, and several on
    /// one character are drawn stacked, as high or as deep as they go. Every nonspacing and enclosing mark
    /// (Mn and Me) of the table below, the same as the Windows mirror's, so neither platform leans on its
    /// own Unicode tables. A spacing mark (Mc) takes room of its own, as a letter does, and is not one.
    static func isCombiningMark(_ scalar: Unicode.Scalar) -> Bool {
        // The ranges are in order and apart, so a binary search finds the one that could hold the scalar.
        var low = 0, high = combiningMarks.count
        while low < high {
            let middle = (low + high) / 2
            if combiningMarks[middle].upperBound < scalar.value { low = middle + 1 } else { high = middle }
        }
        return low < combiningMarks.count && combiningMarks[low].contains(scalar.value)
    }

    // BEGIN generated combining marks: Unicode 16.0.0, from scripts/generate-combining-marks.py. Do not edit by hand.
    static let combiningMarks: [ClosedRange<UInt32>] = [
        0x0300...0x036F, 0x0483...0x0489, 0x0591...0x05BD, 0x05BF...0x05BF, 0x05C1...0x05C2,
        0x05C4...0x05C5, 0x05C7...0x05C7, 0x0610...0x061A, 0x064B...0x065F, 0x0670...0x0670,
        0x06D6...0x06DC, 0x06DF...0x06E4, 0x06E7...0x06E8, 0x06EA...0x06ED, 0x0711...0x0711,
        0x0730...0x074A, 0x07A6...0x07B0, 0x07EB...0x07F3, 0x07FD...0x07FD, 0x0816...0x0819,
        0x081B...0x0823, 0x0825...0x0827, 0x0829...0x082D, 0x0859...0x085B, 0x0897...0x089F,
        0x08CA...0x08E1, 0x08E3...0x0902, 0x093A...0x093A, 0x093C...0x093C, 0x0941...0x0948,
        0x094D...0x094D, 0x0951...0x0957, 0x0962...0x0963, 0x0981...0x0981, 0x09BC...0x09BC,
        0x09C1...0x09C4, 0x09CD...0x09CD, 0x09E2...0x09E3, 0x09FE...0x09FE, 0x0A01...0x0A02,
        0x0A3C...0x0A3C, 0x0A41...0x0A42, 0x0A47...0x0A48, 0x0A4B...0x0A4D, 0x0A51...0x0A51,
        0x0A70...0x0A71, 0x0A75...0x0A75, 0x0A81...0x0A82, 0x0ABC...0x0ABC, 0x0AC1...0x0AC5,
        0x0AC7...0x0AC8, 0x0ACD...0x0ACD, 0x0AE2...0x0AE3, 0x0AFA...0x0AFF, 0x0B01...0x0B01,
        0x0B3C...0x0B3C, 0x0B3F...0x0B3F, 0x0B41...0x0B44, 0x0B4D...0x0B4D, 0x0B55...0x0B56,
        0x0B62...0x0B63, 0x0B82...0x0B82, 0x0BC0...0x0BC0, 0x0BCD...0x0BCD, 0x0C00...0x0C00,
        0x0C04...0x0C04, 0x0C3C...0x0C3C, 0x0C3E...0x0C40, 0x0C46...0x0C48, 0x0C4A...0x0C4D,
        0x0C55...0x0C56, 0x0C62...0x0C63, 0x0C81...0x0C81, 0x0CBC...0x0CBC, 0x0CBF...0x0CBF,
        0x0CC6...0x0CC6, 0x0CCC...0x0CCD, 0x0CE2...0x0CE3, 0x0D00...0x0D01, 0x0D3B...0x0D3C,
        0x0D41...0x0D44, 0x0D4D...0x0D4D, 0x0D62...0x0D63, 0x0D81...0x0D81, 0x0DCA...0x0DCA,
        0x0DD2...0x0DD4, 0x0DD6...0x0DD6, 0x0E31...0x0E31, 0x0E34...0x0E3A, 0x0E47...0x0E4E,
        0x0EB1...0x0EB1, 0x0EB4...0x0EBC, 0x0EC8...0x0ECE, 0x0F18...0x0F19, 0x0F35...0x0F35,
        0x0F37...0x0F37, 0x0F39...0x0F39, 0x0F71...0x0F7E, 0x0F80...0x0F84, 0x0F86...0x0F87,
        0x0F8D...0x0F97, 0x0F99...0x0FBC, 0x0FC6...0x0FC6, 0x102D...0x1030, 0x1032...0x1037,
        0x1039...0x103A, 0x103D...0x103E, 0x1058...0x1059, 0x105E...0x1060, 0x1071...0x1074,
        0x1082...0x1082, 0x1085...0x1086, 0x108D...0x108D, 0x109D...0x109D, 0x135D...0x135F,
        0x1712...0x1714, 0x1732...0x1733, 0x1752...0x1753, 0x1772...0x1773, 0x17B4...0x17B5,
        0x17B7...0x17BD, 0x17C6...0x17C6, 0x17C9...0x17D3, 0x17DD...0x17DD, 0x180B...0x180D,
        0x180F...0x180F, 0x1885...0x1886, 0x18A9...0x18A9, 0x1920...0x1922, 0x1927...0x1928,
        0x1932...0x1932, 0x1939...0x193B, 0x1A17...0x1A18, 0x1A1B...0x1A1B, 0x1A56...0x1A56,
        0x1A58...0x1A5E, 0x1A60...0x1A60, 0x1A62...0x1A62, 0x1A65...0x1A6C, 0x1A73...0x1A7C,
        0x1A7F...0x1A7F, 0x1AB0...0x1ACE, 0x1B00...0x1B03, 0x1B34...0x1B34, 0x1B36...0x1B3A,
        0x1B3C...0x1B3C, 0x1B42...0x1B42, 0x1B6B...0x1B73, 0x1B80...0x1B81, 0x1BA2...0x1BA5,
        0x1BA8...0x1BA9, 0x1BAB...0x1BAD, 0x1BE6...0x1BE6, 0x1BE8...0x1BE9, 0x1BED...0x1BED,
        0x1BEF...0x1BF1, 0x1C2C...0x1C33, 0x1C36...0x1C37, 0x1CD0...0x1CD2, 0x1CD4...0x1CE0,
        0x1CE2...0x1CE8, 0x1CED...0x1CED, 0x1CF4...0x1CF4, 0x1CF8...0x1CF9, 0x1DC0...0x1DFF,
        0x20D0...0x20F0, 0x2CEF...0x2CF1, 0x2D7F...0x2D7F, 0x2DE0...0x2DFF, 0x302A...0x302D,
        0x3099...0x309A, 0xA66F...0xA672, 0xA674...0xA67D, 0xA69E...0xA69F, 0xA6F0...0xA6F1,
        0xA802...0xA802, 0xA806...0xA806, 0xA80B...0xA80B, 0xA825...0xA826, 0xA82C...0xA82C,
        0xA8C4...0xA8C5, 0xA8E0...0xA8F1, 0xA8FF...0xA8FF, 0xA926...0xA92D, 0xA947...0xA951,
        0xA980...0xA982, 0xA9B3...0xA9B3, 0xA9B6...0xA9B9, 0xA9BC...0xA9BD, 0xA9E5...0xA9E5,
        0xAA29...0xAA2E, 0xAA31...0xAA32, 0xAA35...0xAA36, 0xAA43...0xAA43, 0xAA4C...0xAA4C,
        0xAA7C...0xAA7C, 0xAAB0...0xAAB0, 0xAAB2...0xAAB4, 0xAAB7...0xAAB8, 0xAABE...0xAABF,
        0xAAC1...0xAAC1, 0xAAEC...0xAAED, 0xAAF6...0xAAF6, 0xABE5...0xABE5, 0xABE8...0xABE8,
        0xABED...0xABED, 0xFB1E...0xFB1E, 0xFE00...0xFE0F, 0xFE20...0xFE2F, 0x101FD...0x101FD,
        0x102E0...0x102E0, 0x10376...0x1037A, 0x10A01...0x10A03, 0x10A05...0x10A06, 0x10A0C...0x10A0F,
        0x10A38...0x10A3A, 0x10A3F...0x10A3F, 0x10AE5...0x10AE6, 0x10D24...0x10D27, 0x10D69...0x10D6D,
        0x10EAB...0x10EAC, 0x10EFC...0x10EFF, 0x10F46...0x10F50, 0x10F82...0x10F85, 0x11001...0x11001,
        0x11038...0x11046, 0x11070...0x11070, 0x11073...0x11074, 0x1107F...0x11081, 0x110B3...0x110B6,
        0x110B9...0x110BA, 0x110C2...0x110C2, 0x11100...0x11102, 0x11127...0x1112B, 0x1112D...0x11134,
        0x11173...0x11173, 0x11180...0x11181, 0x111B6...0x111BE, 0x111C9...0x111CC, 0x111CF...0x111CF,
        0x1122F...0x11231, 0x11234...0x11234, 0x11236...0x11237, 0x1123E...0x1123E, 0x11241...0x11241,
        0x112DF...0x112DF, 0x112E3...0x112EA, 0x11300...0x11301, 0x1133B...0x1133C, 0x11340...0x11340,
        0x11366...0x1136C, 0x11370...0x11374, 0x113BB...0x113C0, 0x113CE...0x113CE, 0x113D0...0x113D0,
        0x113D2...0x113D2, 0x113E1...0x113E2, 0x11438...0x1143F, 0x11442...0x11444, 0x11446...0x11446,
        0x1145E...0x1145E, 0x114B3...0x114B8, 0x114BA...0x114BA, 0x114BF...0x114C0, 0x114C2...0x114C3,
        0x115B2...0x115B5, 0x115BC...0x115BD, 0x115BF...0x115C0, 0x115DC...0x115DD, 0x11633...0x1163A,
        0x1163D...0x1163D, 0x1163F...0x11640, 0x116AB...0x116AB, 0x116AD...0x116AD, 0x116B0...0x116B5,
        0x116B7...0x116B7, 0x1171D...0x1171D, 0x1171F...0x1171F, 0x11722...0x11725, 0x11727...0x1172B,
        0x1182F...0x11837, 0x11839...0x1183A, 0x1193B...0x1193C, 0x1193E...0x1193E, 0x11943...0x11943,
        0x119D4...0x119D7, 0x119DA...0x119DB, 0x119E0...0x119E0, 0x11A01...0x11A0A, 0x11A33...0x11A38,
        0x11A3B...0x11A3E, 0x11A47...0x11A47, 0x11A51...0x11A56, 0x11A59...0x11A5B, 0x11A8A...0x11A96,
        0x11A98...0x11A99, 0x11C30...0x11C36, 0x11C38...0x11C3D, 0x11C3F...0x11C3F, 0x11C92...0x11CA7,
        0x11CAA...0x11CB0, 0x11CB2...0x11CB3, 0x11CB5...0x11CB6, 0x11D31...0x11D36, 0x11D3A...0x11D3A,
        0x11D3C...0x11D3D, 0x11D3F...0x11D45, 0x11D47...0x11D47, 0x11D90...0x11D91, 0x11D95...0x11D95,
        0x11D97...0x11D97, 0x11EF3...0x11EF4, 0x11F00...0x11F01, 0x11F36...0x11F3A, 0x11F40...0x11F40,
        0x11F42...0x11F42, 0x11F5A...0x11F5A, 0x13440...0x13440, 0x13447...0x13455, 0x1611E...0x16129,
        0x1612D...0x1612F, 0x16AF0...0x16AF4, 0x16B30...0x16B36, 0x16F4F...0x16F4F, 0x16F8F...0x16F92,
        0x16FE4...0x16FE4, 0x1BC9D...0x1BC9E, 0x1CF00...0x1CF2D, 0x1CF30...0x1CF46, 0x1D167...0x1D169,
        0x1D17B...0x1D182, 0x1D185...0x1D18B, 0x1D1AA...0x1D1AD, 0x1D242...0x1D244, 0x1DA00...0x1DA36,
        0x1DA3B...0x1DA6C, 0x1DA75...0x1DA75, 0x1DA84...0x1DA84, 0x1DA9B...0x1DA9F, 0x1DAA1...0x1DAAF,
        0x1E000...0x1E006, 0x1E008...0x1E018, 0x1E01B...0x1E021, 0x1E023...0x1E024, 0x1E026...0x1E02A,
        0x1E08F...0x1E08F, 0x1E130...0x1E136, 0x1E2AE...0x1E2AE, 0x1E2EC...0x1E2EF, 0x1E4EC...0x1E4EF,
        0x1E5EE...0x1E5EF, 0x1E8D0...0x1E8D6, 0x1E944...0x1E94A, 0xE0100...0xE01EF,
    ]
    // END generated combining marks

    /// The rule for combining marks as a text is read from its start: the first two marks on a character
    /// are drawn, and a third or later one, or a mark that starts the text, shows as its code. Two, because
    /// ordinary text needs two — a Thai vowel and its tone, decomposed Vietnamese, a Hebrew dagesh and
    /// vowel, an Arabic shadda and vowel. Even two tall marks reach past a line's own height, so the
    /// dialog clips each line of what would run to its own box, and no mark can draw over another line;
    /// the count keeps a tall stack, cut off by that clip, from hiding the line's own text.
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

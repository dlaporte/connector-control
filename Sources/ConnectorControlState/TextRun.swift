import Foundation

/// Mirror: windows/src/ConnectorControl.Core/State/TextRun.cs
///
/// A piece of a sentence, and whether it is set apart from the words around it: a name someone
/// else chose, in a confirmation, or a command or path in a caption. The views draw a set-apart
/// piece in the monospaced face, so where it begins and ends shows, whatever it holds. Pieces are
/// found scalar by scalar, as the Windows mirror finds them, so both split a sentence alike.
public struct TextRun: Equatable, Sendable {
    /// What a name cut to the limit ends with.
    public static let cutMark = "…"
    static let openingQuote: Unicode.Scalar = "\u{201C}"
    static let closingQuote: Unicode.Scalar = "\u{201D}"

    public let text: String
    public let isSetApart: Bool

    public init(_ text: String, isSetApart: Bool = false) {
        self.text = text
        self.isSetApart = isSetApart
    }

    // Platform idiom: the Mac isolates a set-apart piece (U+2068 … U+2069); WPF's text engine predates
    // isolates, so the Windows mirror embeds it left to right (U+202A … U+202C), which keeps the neutral
    // text between two pieces on the paragraph's side as well. U+2068 takes the direction of the piece's
    // first strong letter where U+202A sets left to right, so a name that mixes directions reads in a
    // different order inside its piece on each platform.
    /// The text as a view draws it: a set-apart piece isolated, so a name that runs right to left cannot
    /// pull the quotes or the names beside it into its own order; words as they are.
    public var shownText: String { isSetApart ? "\u{2068}" + text + "\u{2069}" : text }

    // Platform idiom: text is compared scalar by scalar, as the Windows mirror's record compares it
    // ordinally by itself; Swift's == would hold canonically equivalent text equal, and a name composed
    // another way is another name.
    public static func == (one: TextRun, other: TextRun) -> Bool {
        one.isSetApart == other.isSetApart && same(one.text, other.text)
    }

    /// The same text, scalar for scalar.
    static func same(_ one: String, _ other: String) -> Bool {
        one.unicodeScalars.elementsEqual(other.unicodeScalars)
    }

    /// `sentence` with every occurrence of each of `pieces` set apart — at each place, the first of
    /// `pieces` found there — and the rest as words. An empty piece sets nothing apart.
    public static func runs(_ sentence: String, settingApart pieces: [String]) -> [TextRun] {
        let wanted = pieces.filter { !$0.isEmpty }.map { Array($0.unicodeScalars) }
        let scalars = Array(sentence.unicodeScalars)
        var runs: [TextRun] = []
        var words: [Unicode.Scalar] = []
        var index = 0
        while index < scalars.count {
            if let piece = wanted.first(where: { index + $0.count <= scalars.count && Array(scalars[index..<index + $0.count]) == $0 }) {
                append(&runs, TextRun(text(words)))
                words = []
                runs.append(TextRun(text(piece), isSetApart: true))
                index += piece.count
            } else {
                words.append(scalars[index])
                index += 1
            }
        }
        append(&runs, TextRun(text(words)))
        return runs
    }

    /// One of the app's sentences with every name between its own quotes set apart: the text between
    /// each “ and the ” after it. A name's own “ and ” are escaped before it reaches a sentence, so
    /// every raw one is the sentence's. A name longer than `limit` scalars is cut in the middle (see
    /// `cut(_:_:)`); an empty name, or a “ never closed, stays words.
    public static func names(in sentence: String, limit: Int) -> [TextRun] {
        var runs: [TextRun] = []
        var words: [Unicode.Scalar] = []
        var name: [Unicode.Scalar]?
        for scalar in sentence.unicodeScalars {
            guard name != nil else {
                words.append(scalar)
                if scalar == openingQuote { name = [] }
                continue
            }
            guard scalar == closingQuote else {
                name?.append(scalar)
                continue
            }
            if let held = name, !held.isEmpty {
                append(&runs, TextRun(text(words)))
                words = []
                runs.append(TextRun(text(cut(held, limit)), isSetApart: true))
            }
            words.append(scalar)
            name = nil
        }
        words += name ?? []
        append(&runs, TextRun(text(words)))
        return runs
    }

    /// `name` no longer than `limit` scalars: when it is longer, as much of its start and of its end as
    /// fit with `cutMark` between them, the end given the odd scalar. Names that share a long start,
    /// as a team's often do, still read apart by their ends. A name comes here escaped, and an escape is
    /// kept or left out whole, never split into a piece that reads as something else. The end never
    /// starts with a combining mark: it would be drawn on the "…", so it goes with the character it was on.
    static func cut(_ name: [Unicode.Scalar], _ limit: Int) -> [Unicode.Scalar] {
        guard name.count > limit else { return name }
        let pieces = escapes(name)
        let room = limit - cutMark.unicodeScalars.count
        let start = fitting(pieces, room / 2)
        let end = fitting(pieces.reversed(), room - room / 2).reversed()
            .drop { $0.count == 1 && ControlText.isCombiningMark($0[0]) }
        return start.flatMap { $0 } + Array(cutMark.unicodeScalars) + end.flatMap { $0 }
    }

    /// An escaped name cut as `cut(_:_:)` cuts one.
    static func cutName(_ name: String, limit: Int) -> String {
        text(cut(Array(name.unicodeScalars), limit))
    }

    /// An escaped name in pieces: each escape whole — a backslash and the character after it, or
    /// `\u{…}` to its closing brace — and every other scalar alone.
    static func escapes(_ name: [Unicode.Scalar]) -> [[Unicode.Scalar]] {
        var pieces: [[Unicode.Scalar]] = []
        var index = 0
        while index < name.count {
            var end = index + 1
            if name[index] == "\\", end < name.count {
                end += 1
                if name[index + 1] == "u", end < name.count, name[end] == "{",
                   let close = name[end...].firstIndex(of: "}") {
                    end = close + 1
                }
            }
            pieces.append(Array(name[index..<end]))
            index = end
        }
        return pieces
    }

    /// The leading pieces that fit in `room` scalars, stopping at the first that does not.
    static func fitting<Pieces: Sequence>(_ pieces: Pieces, _ room: Int) -> [[Unicode.Scalar]] where Pieces.Element == [Unicode.Scalar] {
        var taken: [[Unicode.Scalar]] = []
        var used = 0
        for piece in pieces {
            guard used + piece.count <= room else { break }
            taken.append(piece)
            used += piece.count
        }
        return taken
    }

    // Platform idiom: the Windows mirror calls this Join, since there Text is the record's own property.
    /// The scalars as text.
    static func text(_ scalars: [Unicode.Scalar]) -> String {
        var view = String.UnicodeScalarView()
        view.append(contentsOf: scalars)
        return String(view)
    }

    /// Adds a run of words to the run of words before it, or a run of its own; an empty one adds nothing.
    static func append(_ runs: inout [TextRun], _ run: TextRun) {
        guard !run.text.isEmpty else { return }
        if !run.isSetApart, let last = runs.last, !last.isSetApart {
            runs[runs.count - 1] = TextRun(last.text + run.text)
        } else {
            runs.append(run)
        }
    }
}

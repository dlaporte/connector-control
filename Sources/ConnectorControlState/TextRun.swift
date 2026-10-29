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
    // text between two pieces on the paragraph's side as well.
    /// The text as a view draws it: a set-apart piece isolated, so a name that runs right to left cannot
    /// pull the quotes or the names beside it into its own order; words as they are.
    public var shownText: String { isSetApart ? "\u{2068}" + text + "\u{2069}" : text }

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
    /// every raw one is the sentence's. A name longer than `limit` scalars is cut to `limit - 1` and
    /// `cutMark`; an empty name, or a “ never closed, stays words.
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

    /// `name` no longer than `limit` scalars, the last of them `cutMark` when it was longer.
    static func cut(_ name: [Unicode.Scalar], _ limit: Int) -> [Unicode.Scalar] {
        name.count <= limit ? name : Array(name.prefix(limit - 1)) + Array(cutMark.unicodeScalars)
    }

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

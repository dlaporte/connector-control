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
    /// continues the one above it.
    public static func labelled(_ rows: [(String, String)]) -> [String] {
        let column = (rows.map { width($0.0) }.max() ?? 0) + gap
        return rows.map { trimmingTrailingSpaces(pad($0.0, to: column) + $0.1) }
    }

    /// A table: every column but the last padded to its widest cell plus the gap; the header row,
    /// when there is one, first. No line ends in spaces, whatever its last cells hold.
    public static func table(header: [String]?, rows: [[String]]) -> [String] {
        let all = (header.map { [$0] } ?? []) + rows
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

    /// “a”, “a” and “b”, “a”, “b” and “c” — names as the app's sentences list them.
    public static func quotedList(_ names: [String]) -> String {
        let quoted = names.map { "“\($0)”" }
        guard let last = quoted.last else { return "" }
        guard quoted.count > 1 else { return last }
        return quoted.dropLast().joined(separator: ", ") + listAnd + last
    }
}

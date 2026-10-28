using System.Text;

namespace ConnectorControl.Core.State;

/// <summary>
/// The layout of the control layer's text results: label/value lines and tables, padded with
/// spaces so they line up in a terminal. Widths count Unicode scalars, which the Mac mirror
/// counts the same way, so the two platforms pad every line identically.
///
/// Mirror: Sources/ConnectorControlState/Control/ControlText.swift
/// </summary>
public static class ControlText
{
    public const string ListAnd = " and ";
    /// <summary>The spaces after the widest cell of a column.</summary>
    internal const int Gap = 3;

    // Platform idiom: runes here, a .NET string being UTF-16; the Mac mirror counts Unicode scalars.
    public static int Width(string text) => text.EnumerateRunes().Count();

    public static string Pad(string text, int width) =>
        text + new string(' ', Math.Max(0, width - Width(text)));

    /// <summary>"label   value" lines, every value starting in the same column. A row with an empty label
    /// continues the one above it.</summary>
    public static IReadOnlyList<string> Labelled(IReadOnlyList<(string Label, string Value)> rows)
    {
        var column = (rows.Count == 0 ? 0 : rows.Max(row => Width(row.Label))) + Gap;
        return rows.Select(row => TrimmingTrailingSpaces(Pad(row.Label, column) + row.Value)).ToList();
    }

    /// <summary>A table: every column but the last padded to its widest cell plus the gap; the header row,
    /// when there is one, first. No line ends in spaces, whatever its last cells hold.</summary>
    public static IReadOnlyList<string> Table(IReadOnlyList<string>? header, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var all = (header is null ? [] : new List<IReadOnlyList<string>> { header }).Concat(rows).ToList();
        var columns = all.Count == 0 ? 0 : all.Max(cells => cells.Count);
        var widths = Enumerable.Range(0, columns)
            .Select(index => all.Max(cells => index < cells.Count ? Width(cells[index]) : 0) + Gap)
            .ToList();
        return all.Select(cells =>
        {
            var line = new StringBuilder();
            for (var index = 0; index < cells.Count; index++)
            {
                line.Append(index == cells.Count - 1 ? cells[index] : Pad(cells[index], widths[index]));
            }
            return TrimmingTrailingSpaces(line.ToString());
        }).ToList();
    }

    // Platform idiom: TrimEnd strips the spaces in one call here; the Mac mirror strips them one at a time.
    internal static string TrimmingTrailingSpaces(string line) => line.TrimEnd(' ');

    /// <summary>“a”, “a” and “b”, “a”, “b” and “c” — names as the app's sentences list them.</summary>
    public static string QuotedList(IReadOnlyList<string> names)
    {
        var quoted = names.Select(name => $"“{name}”").ToList();
        if (quoted.Count == 0)
        {
            return "";
        }
        if (quoted.Count == 1)
        {
            return quoted[0];
        }
        return string.Join(", ", quoted.Take(quoted.Count - 1)) + ListAnd + quoted[^1];
    }
}

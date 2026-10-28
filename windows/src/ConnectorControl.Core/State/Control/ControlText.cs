using System.Globalization;
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
    /// continues the one above it. Labels and values go through <see cref="Escaped(string)"/>.</summary>
    public static IReadOnlyList<string> Labelled(IReadOnlyList<(string Label, string Value)> rows)
    {
        var escaped = rows.Select(row => (Label: Escaped(row.Label), Value: Escaped(row.Value))).ToList();
        var column = (escaped.Count == 0 ? 0 : escaped.Max(row => Width(row.Label))) + Gap;
        return escaped.Select(row => TrimmingTrailingSpaces(Pad(row.Label, column) + row.Value)).ToList();
    }

    /// <summary>A table: every column but the last padded to its widest cell plus the gap; the header row,
    /// when there is one, first. No line ends in spaces, whatever its last cells hold. Every cell
    /// goes through <see cref="Escaped(string)"/>.</summary>
    public static IReadOnlyList<string> Table(IReadOnlyList<string>? header, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var all = (header is null ? [] : new List<IReadOnlyList<string>> { header }).Concat(rows)
            .Select(cells => (IReadOnlyList<string>)cells.Select(Escaped).ToList()).ToList();
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

    /// <summary>Text as a line shows it: a control character, and a format character that reorders or hides
    /// text (U+00AD, U+061C, U+200B–U+200F, U+2028–U+202E, U+2060–U+2069, U+FEFF), as a visible
    /// escape — <c>\n</c>, <c>\r</c>, <c>\t</c> or <c>\u{202E}</c> — so a value can neither break its line nor hide part
    /// of it.</summary>
    public static string Escaped(string text)
    {
        var line = new StringBuilder();
        foreach (var c in text)
        {
            line.Append(Escape(c) ?? c.ToString());
        }
        return line.ToString();
    }

    /// <summary>One of the app's own sentences that quotes names someone else chose: each of
    /// <paramref name="names"/> as <see cref="EscapedName"/> shows it, and the rest — the sentence's own quotes
    /// included — as <see cref="Escaped(string)"/> does. A name with nothing to escape reads as itself, so
    /// replacing every place it appears can only ever escape more, never less.</summary>
    public static string Escaped(string text, IReadOnlyList<string> names)
    {
        var sentence = text;
        // Platform idiom: replaced ordinally, as the Mac mirror replaces literally; a culture-aware
        // replace would also take a canonically equivalent spelling of a name.
        foreach (var name in names.Where(name => name.Length > 0))
        {
            sentence = sentence.Replace(name, EscapedName(name), StringComparison.Ordinal);
        }
        return Escaped(sentence);
    }

    /// <summary>A name as the app's sentences quote it: as <see cref="Escaped(string)"/> shows it, with “ and ” escaped as
    /// well, so a name can neither break its line nor close its own quotes and read as two names.</summary>
    public static string EscapedName(string text)
    {
        var name = new StringBuilder();
        foreach (var c in text)
        {
            name.Append(Escape(c) ?? (c is '\u201C' or '\u201D' ? HexEscape(c) : c.ToString()));
        }
        return name.ToString();
    }

    // Platform idiom: one UTF-16 unit at a time here, one scalar on the Mac; every character escaped
    // is in the Basic Plane, so both escape the same ones.
    /// <summary>The escape for one character, or null for one shown as it is.</summary>
    internal static string? Escape(char c) => c switch
    {
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        <= '\u001F' or (>= '\u007F' and <= '\u009F') or '\u00AD' or '\u061C' or (>= '\u200B' and <= '\u200F')
            or (>= '\u2028' and <= '\u202E') or (>= '\u2060' and <= '\u2069') or '\uFEFF' => HexEscape(c),
        _ => null,
    };

    /// <summary>A character by its code, as <c>\u{202E}</c>: every one escaped is in the Basic Plane, so four hex digits.</summary>
    internal static string HexEscape(char c) => $"\\u{{{(int)c:X4}}}";

    /// <summary>One argument of a command line: as written when it reads as one word — not empty, with no
    /// space, no ' and nothing <see cref="Escaped(string)"/> escapes — else between single quotes, with \ and '
    /// escaped inside as well, so no two argument lists read alike.</summary>
    public static string Word(string text)
    {
        if (text.Length > 0 && !text.Any(c => IsSpace(c) || c == '\'' || Escape(c) is not null))
        {
            return text;
        }
        var word = new StringBuilder("'");
        foreach (var c in text)
        {
            word.Append(c switch
            {
                '\\' => "\\\\",
                '\'' => "\\'",
                _ => Escape(c) ?? c.ToString(),
            });
        }
        return word.Append('\'').ToString();
    }

    /// <summary>An argument list on one line: each through <see cref="Word"/>, between spaces.</summary>
    public static string Words(IReadOnlyList<string> args) =>
        string.Join(" ", args.Select(Word));

    /// <summary>A space, as <see cref="StringExtensions.TrimSpaces"/> reads one: any space separator.</summary>
    internal static bool IsSpace(char c) =>
        CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    /// <summary>“a”, “a” and “b”, “a”, “b” and “c” — names as the app's sentences list them, each through
    /// <see cref="EscapedName"/>.</summary>
    public static string QuotedList(IReadOnlyList<string> names)
    {
        var quoted = names.Select(name => $"“{EscapedName(name)}”").ToList();
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

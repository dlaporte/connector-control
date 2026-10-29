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
    /// continues the one above it. Labels and values go through <see cref="EscapedValue"/>.</summary>
    public static IReadOnlyList<string> Labelled(IReadOnlyList<(string Label, string Value)> rows)
    {
        var escaped = rows.Select(row => (Label: EscapedValue(row.Label), Value: EscapedValue(row.Value))).ToList();
        var column = (escaped.Count == 0 ? 0 : escaped.Max(row => Width(row.Label))) + Gap;
        return escaped.Select(row => TrimmingTrailingSpaces(Pad(row.Label, column) + row.Value)).ToList();
    }

    /// <summary>A table: every column but the last padded to its widest cell plus the gap; the header row,
    /// when there is one, first. No line ends in spaces, whatever its last cells hold. Every cell
    /// goes through <see cref="Escaped(string)"/>; a cell that holds a name comes in through <see cref="EscapedName"/>.</summary>
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

    /// <summary>Text as a line shows it: a control character, and a character that shows as nothing or as a
    /// blank — every Default_Ignorable code point (soft hyphen, joiners, direction marks, variation
    /// selectors, tags…), the blank letters (U+115F, U+1160, U+2800, U+3164, U+FFA0), the interlinear
    /// annotation marks — as a visible escape, <c>\n</c>, <c>\r</c>, <c>\t</c> or <c>\u{202E}</c>, so a value
    /// can neither break its line, nor hide part of it, nor pass for another.</summary>
    public static string Escaped(string text)
    {
        var line = new StringBuilder();
        foreach (var scalar in Scalars(text))
        {
            line.Append(Escape(scalar) ?? Text(scalar));
        }
        return line.ToString();
    }

    /// <summary>One of the app's own sentences that quotes names someone else chose, read once from the left: at
    /// each place, the longest of <paramref name="names"/> whose quoted form — “, the name, ” — starts there is shown as
    /// “ + <see cref="EscapedName"/> + ”, and anything else — the sentence's own quotes included — as
    /// <see cref="Escaped(string)"/> does. A name is found only between the app's own quotes, so a name that begins with “ can never
    /// match at another name's opening quote, and no name's escape is ever read again as part of another
    /// name. Names are matched ordinally, as the Mac mirror matches them scalar by scalar; equal lengths in
    /// ordinal order, so both platforms pick the same name.</summary>
    public static string Escaped(string text, IReadOnlyList<string> names)
    {
        var ordered = names.Where(name => name.Length > 0)
            .OrderByDescending(name => Scalars(name).Count())
            .ThenBy(name => name, StringComparer.Ordinal)
            .Select(name => (Name: name, Quoted: Scalars(name).Prepend(OpeningQuote).Append(ClosingQuote).ToList()))
            .ToList();
        var scalars = Scalars(text).ToList();
        var line = new StringBuilder();
        var index = 0;
        while (index < scalars.Count)
        {
            var at = index;
            var found = ordered.FirstOrDefault(candidate => at + candidate.Quoted.Count <= scalars.Count
                && scalars.Skip(at).Take(candidate.Quoted.Count).SequenceEqual(candidate.Quoted));
            if (found.Name is not null)
            {
                line.Append('\u201C').Append(EscapedName(found.Name)).Append('\u201D');
                index += found.Quoted.Count;
            }
            else
            {
                line.Append(Escape(scalars[index]) ?? Text(scalars[index]));
                index++;
            }
        }
        return line.ToString();
    }

    /// <summary>A name as the app's sentences quote it: as <see cref="Escaped(string)"/> shows it, with “ and ” escaped as
    /// well, so a name can neither break its line nor close its own quotes and read as two names, and
    /// a backslash doubled, as <see cref="Word"/> doubles one, so no two names read alike.</summary>
    public static string EscapedName(string text)
    {
        var name = new StringBuilder();
        foreach (var scalar in Scalars(text))
        {
            name.Append(scalar switch
            {
                Backslash => "\\\\",
                OpeningQuote or ClosingQuote => HexEscape(scalar),
                _ => EscapeShown(scalar) ?? Text(scalar),
            });
        }
        return name.ToString();
    }

    /// <summary>A key or a value on a line: as <see cref="Escaped(string)"/> shows it, and a space-like character as its code, since
    /// there it could pass for the ordinary space. Backslashes stay as they are: a value may be a path.</summary>
    public static string EscapedValue(string text)
    {
        var value = new StringBuilder();
        foreach (var scalar in Scalars(text))
        {
            value.Append(EscapeShown(scalar) ?? Text(scalar));
        }
        return value.ToString();
    }

    /// <summary>A path as a dialog's footer quotes it: as <see cref="Escaped(string)"/> shows it, with “ and ” escaped as
    /// well so it cannot close the app's quotes around it, and its backslashes as they are — on Windows they are
    /// the path's own separators.</summary>
    public static string EscapedPath(string text)
    {
        var path = new StringBuilder();
        foreach (var scalar in Scalars(text))
        {
            path.Append(scalar is OpeningQuote or ClosingQuote ? HexEscape(scalar) : EscapeShown(scalar) ?? Text(scalar));
        }
        return path.ToString();
    }

    private const int Backslash = '\\';
    private const int Apostrophe = '\'';
    private const int OpeningQuote = '\u201C';
    private const int ClosingQuote = '\u201D';

    // Platform idiom: the Mac walks a string's scalars; a .NET string is UTF-16, so this walks the same
    // scalars itself, and keeps a lone surrogate, which only it can hold, as its own unit.
    /// <summary>The text's scalars in order: a surrogate pair as its code point, a lone surrogate as itself.</summary>
    internal static IEnumerable<int> Scalars(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                yield return char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            else
            {
                yield return text[i];
            }
        }
    }

    /// <summary>One scalar as text; a lone surrogate goes back as the unit it was.</summary>
    internal static string Text(int scalar) =>
        scalar is >= 0xD800 and <= 0xDFFF ? ((char)scalar).ToString() : char.ConvertFromUtf32(scalar);

    /// <summary>The escape for one character, or null for one shown as it is.</summary>
    internal static string? Escape(int scalar) => scalar switch
    {
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        <= 0x1F or (>= 0x7F and <= 0x9F) or 0xAD or 0x034F or 0x061C or 0x115F or 0x1160 or 0x17B4 or 0x17B5
            or (>= 0x180B and <= 0x180F) or (>= 0x200B and <= 0x200F) or (>= 0x2028 and <= 0x202E)
            or (>= 0x2060 and <= 0x206F) or 0x2800 or 0x3164 or (>= 0xFE00 and <= 0xFE0F) or 0xFEFF or 0xFFA0
            or (>= 0xFFF0 and <= 0xFFFB) or (>= 0x1BCA0 and <= 0x1BCA3) or (>= 0x1D173 and <= 0x1D17A)
            or (>= 0xE0000 and <= 0xE0FFF) => HexEscape(scalar),
        // Platform idiom: a lone surrogate, which a Swift string cannot hold, shows as its code.
        >= 0xD800 and <= 0xDFFF => HexEscape(scalar),
        _ => null,
    };

    /// <summary>The escape for one character of a name, a key, a value or a path: as <see cref="Escape"/>, and a space
    /// that is not the ordinary one too (U+00A0, U+1680, U+2000–U+200A, U+202F, U+205F, U+3000), which could pass for
    /// it there. Free text keeps them: a localized system message sets its punctuation off with one.</summary>
    internal static string? EscapeShown(int scalar) => scalar switch
    {
        0xA0 or 0x1680 or (>= 0x2000 and <= 0x200A) or 0x202F or 0x205F or 0x3000 => HexEscape(scalar),
        _ => Escape(scalar),
    };

    /// <summary>A character by its code, as <c>\u{202E}</c>: at least four hex digits, five beyond the Basic Plane.</summary>
    internal static string HexEscape(int scalar) => $"\\u{{{scalar:X4}}}";

    /// <summary>One argument of a command line: as written when it reads as one word — not empty, with no
    /// space, no ' and nothing <see cref="Escaped(string)"/> escapes — else between single quotes, with \ and '
    /// escaped inside as well, so no two argument lists read alike.</summary>
    public static string Word(string text)
    {
        var scalars = Scalars(text).ToList();
        if (scalars.Count > 0 && !scalars.Any(scalar => IsSpace(scalar) || scalar == Apostrophe || EscapeShown(scalar) is not null))
        {
            return text;
        }
        var word = new StringBuilder("'");
        foreach (var scalar in scalars)
        {
            word.Append(scalar switch
            {
                Backslash => "\\\\",
                Apostrophe => "\\'",
                _ => EscapeShown(scalar) ?? Text(scalar),
            });
        }
        return word.Append('\'').ToString();
    }

    /// <summary>An argument list on one line: each through <see cref="Word"/>, between spaces.</summary>
    public static string Words(IReadOnlyList<string> args) =>
        string.Join(" ", args.Select(Word));

    /// <summary>A space, as <see cref="StringExtensions.TrimSpaces"/> reads one: any space separator.</summary>
    internal static bool IsSpace(int scalar) =>
        scalar is < 0xD800 or > 0xDFFF && Rune.GetUnicodeCategory(new Rune(scalar)) == UnicodeCategory.SpaceSeparator;

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

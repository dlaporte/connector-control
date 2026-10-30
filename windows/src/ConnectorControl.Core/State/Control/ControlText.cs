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
    /// blank - every Default_Ignorable code point (soft hyphen, joiners, direction marks, variation
    /// selectors, tags…), the blank letters (U+115F, U+1160, U+2800, U+3164, U+FFA0), the interlinear
    /// annotation marks, the object replacement character - as a visible escape, <c>\n</c>, <c>\r</c>, <c>\t</c> or <c>\u{202E}</c>, so a value
    /// can neither break its line, nor hide part of it, nor pass for another. A combining mark past the
    /// second on a character, or with no character to sit on, shows as its code too (see <see cref="Marks"/>).</summary>
    public static string Escaped(string text)
    {
        var line = new StringBuilder();
        var marks = new Marks();
        foreach (var scalar in Scalars(text))
        {
            line.Append(marks.Bare(scalar) ? HexEscape(scalar) : Escape(scalar) ?? Text(scalar));
        }
        return line.ToString();
    }

    /// <summary>One of the app's own sentences that quotes names someone else chose, read once from the left: at
    /// each place, the longest of <paramref name="names"/> whose quoted form - “, the name, ” - starts there is shown as
    /// “ + <see cref="EscapedName"/> + ”, and anything else - the sentence's own quotes included - as
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
        var marks = new Marks();
        while (index < scalars.Count)
        {
            var at = index;
            var found = ordered.FirstOrDefault(candidate => at + candidate.Quoted.Count <= scalars.Count
                && scalars.Skip(at).Take(candidate.Quoted.Count).SequenceEqual(candidate.Quoted));
            if (found.Name is not null)
            {
                line.Append('\u201C').Append(EscapedName(found.Name)).Append('\u201D');
                index += found.Quoted.Count;
                // The closing quote is a character a mark after it sits on.
                marks = new Marks(atStart: false);
            }
            else
            {
                line.Append(marks.Bare(scalars[index]) ? HexEscape(scalars[index]) : Escape(scalars[index]) ?? Text(scalars[index]));
                index++;
            }
        }
        return line.ToString();
    }

    /// <summary>A name as the app's sentences quote it: as <see cref="Escaped(string)"/> shows it, with “ and ” escaped as
    /// well, so a name can neither break its line nor close its own quotes and read as two names, “…”
    /// escaped, so a name's own cannot pass for where a long name was cut (<see cref="TextRun.Cut"/>), and
    /// a backslash doubled, as <see cref="Word"/> doubles one, so no two names read alike.</summary>
    public static string EscapedName(string text)
    {
        var name = new StringBuilder();
        var marks = new Marks();
        foreach (var scalar in Scalars(text))
        {
            var bare = marks.Bare(scalar);
            name.Append(scalar switch
            {
                Backslash => "\\\\",
                OpeningQuote or ClosingQuote or Ellipsis => HexEscape(scalar),
                _ => bare ? HexEscape(scalar) : EscapeShown(scalar) ?? Text(scalar),
            });
        }
        return name.ToString();
    }

    /// <summary>A key or a value on a line: as <see cref="Escaped(string)"/> shows it, and a space-like character as its code, since
    /// there it could pass for the ordinary space. Backslashes stay as they are: a value may be a path.</summary>
    public static string EscapedValue(string text)
    {
        var value = new StringBuilder();
        var marks = new Marks();
        foreach (var scalar in Scalars(text))
        {
            value.Append(marks.Bare(scalar) ? HexEscape(scalar) : EscapeShown(scalar) ?? Text(scalar));
        }
        return value.ToString();
    }

    /// <summary>A path as a dialog's footer quotes it: as <see cref="Escaped(string)"/> shows it, with “ and ” escaped as
    /// well so it cannot close the app's quotes around it, and its backslashes as they are - on Windows they are
    /// the path's own separators.</summary>
    public static string EscapedPath(string text)
    {
        var path = new StringBuilder();
        var marks = new Marks();
        foreach (var scalar in Scalars(text))
        {
            var bare = marks.Bare(scalar);
            path.Append(scalar is OpeningQuote or ClosingQuote || bare ? HexEscape(scalar) : EscapeShown(scalar) ?? Text(scalar));
        }
        return path.ToString();
    }

    private const int Backslash = '\\';
    private const int Apostrophe = '\'';
    private const int OpeningQuote = '\u201C';
    private const int ClosingQuote = '\u201D';
    private const int Ellipsis = '\u2026';

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
            or (>= 0xFFF0 and <= 0xFFFC) or (>= 0x1BCA0 and <= 0x1BCA3) or (>= 0x1D173 and <= 0x1D17A)
            or (>= 0xE0000 and <= 0xE0FFF) => HexEscape(scalar),
        // Platform idiom: a lone surrogate, which a Swift string cannot hold, shows as its code.
        >= 0xD800 and <= 0xDFFF => HexEscape(scalar),
        _ => null,
    };

    /// <summary><paramref name="text"/> with every combining mark left out: what a line's own letters need room for, apart
    /// from any marks stacked on them, which the line's box clips rather than makes room for.</summary>
    public static string WithoutMarks(string text)
    {
        var letters = new StringBuilder();
        foreach (var scalar in Scalars(text))
        {
            if (!IsCombiningMark(scalar))
            {
                letters.Append(Text(scalar));
            }
        }
        return letters.ToString();
    }

    /// <summary>The combining marks: each is drawn over, under or around the character before it, and several on
    /// one character are drawn stacked, as high or as deep as they go. Every nonspacing and enclosing mark
    /// (Mn and Me) of the table below, the same as the Mac mirror's, so neither platform leans on its
    /// own Unicode tables. A spacing mark (Mc) takes room of its own, as a letter does, and is not one.</summary>
    internal static bool IsCombiningMark(int scalar)
    {
        // The ranges are in order and apart, so a binary search finds the one that could hold the scalar.
        int low = 0, high = CombiningMarks.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (CombiningMarks[middle].Last < scalar)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low < CombiningMarks.Length && CombiningMarks[low].First <= scalar && scalar <= CombiningMarks[low].Last;
    }

    // BEGIN generated combining marks: Unicode 16.0.0, from scripts/generate-combining-marks.py. Do not edit by hand.
    internal static readonly (int First, int Last)[] CombiningMarks =
    [
        (0x0300, 0x036F), (0x0483, 0x0489), (0x0591, 0x05BD), (0x05BF, 0x05BF), (0x05C1, 0x05C2),
        (0x05C4, 0x05C5), (0x05C7, 0x05C7), (0x0610, 0x061A), (0x064B, 0x065F), (0x0670, 0x0670),
        (0x06D6, 0x06DC), (0x06DF, 0x06E4), (0x06E7, 0x06E8), (0x06EA, 0x06ED), (0x0711, 0x0711),
        (0x0730, 0x074A), (0x07A6, 0x07B0), (0x07EB, 0x07F3), (0x07FD, 0x07FD), (0x0816, 0x0819),
        (0x081B, 0x0823), (0x0825, 0x0827), (0x0829, 0x082D), (0x0859, 0x085B), (0x0897, 0x089F),
        (0x08CA, 0x08E1), (0x08E3, 0x0902), (0x093A, 0x093A), (0x093C, 0x093C), (0x0941, 0x0948),
        (0x094D, 0x094D), (0x0951, 0x0957), (0x0962, 0x0963), (0x0981, 0x0981), (0x09BC, 0x09BC),
        (0x09C1, 0x09C4), (0x09CD, 0x09CD), (0x09E2, 0x09E3), (0x09FE, 0x09FE), (0x0A01, 0x0A02),
        (0x0A3C, 0x0A3C), (0x0A41, 0x0A42), (0x0A47, 0x0A48), (0x0A4B, 0x0A4D), (0x0A51, 0x0A51),
        (0x0A70, 0x0A71), (0x0A75, 0x0A75), (0x0A81, 0x0A82), (0x0ABC, 0x0ABC), (0x0AC1, 0x0AC5),
        (0x0AC7, 0x0AC8), (0x0ACD, 0x0ACD), (0x0AE2, 0x0AE3), (0x0AFA, 0x0AFF), (0x0B01, 0x0B01),
        (0x0B3C, 0x0B3C), (0x0B3F, 0x0B3F), (0x0B41, 0x0B44), (0x0B4D, 0x0B4D), (0x0B55, 0x0B56),
        (0x0B62, 0x0B63), (0x0B82, 0x0B82), (0x0BC0, 0x0BC0), (0x0BCD, 0x0BCD), (0x0C00, 0x0C00),
        (0x0C04, 0x0C04), (0x0C3C, 0x0C3C), (0x0C3E, 0x0C40), (0x0C46, 0x0C48), (0x0C4A, 0x0C4D),
        (0x0C55, 0x0C56), (0x0C62, 0x0C63), (0x0C81, 0x0C81), (0x0CBC, 0x0CBC), (0x0CBF, 0x0CBF),
        (0x0CC6, 0x0CC6), (0x0CCC, 0x0CCD), (0x0CE2, 0x0CE3), (0x0D00, 0x0D01), (0x0D3B, 0x0D3C),
        (0x0D41, 0x0D44), (0x0D4D, 0x0D4D), (0x0D62, 0x0D63), (0x0D81, 0x0D81), (0x0DCA, 0x0DCA),
        (0x0DD2, 0x0DD4), (0x0DD6, 0x0DD6), (0x0E31, 0x0E31), (0x0E34, 0x0E3A), (0x0E47, 0x0E4E),
        (0x0EB1, 0x0EB1), (0x0EB4, 0x0EBC), (0x0EC8, 0x0ECE), (0x0F18, 0x0F19), (0x0F35, 0x0F35),
        (0x0F37, 0x0F37), (0x0F39, 0x0F39), (0x0F71, 0x0F7E), (0x0F80, 0x0F84), (0x0F86, 0x0F87),
        (0x0F8D, 0x0F97), (0x0F99, 0x0FBC), (0x0FC6, 0x0FC6), (0x102D, 0x1030), (0x1032, 0x1037),
        (0x1039, 0x103A), (0x103D, 0x103E), (0x1058, 0x1059), (0x105E, 0x1060), (0x1071, 0x1074),
        (0x1082, 0x1082), (0x1085, 0x1086), (0x108D, 0x108D), (0x109D, 0x109D), (0x135D, 0x135F),
        (0x1712, 0x1714), (0x1732, 0x1733), (0x1752, 0x1753), (0x1772, 0x1773), (0x17B4, 0x17B5),
        (0x17B7, 0x17BD), (0x17C6, 0x17C6), (0x17C9, 0x17D3), (0x17DD, 0x17DD), (0x180B, 0x180D),
        (0x180F, 0x180F), (0x1885, 0x1886), (0x18A9, 0x18A9), (0x1920, 0x1922), (0x1927, 0x1928),
        (0x1932, 0x1932), (0x1939, 0x193B), (0x1A17, 0x1A18), (0x1A1B, 0x1A1B), (0x1A56, 0x1A56),
        (0x1A58, 0x1A5E), (0x1A60, 0x1A60), (0x1A62, 0x1A62), (0x1A65, 0x1A6C), (0x1A73, 0x1A7C),
        (0x1A7F, 0x1A7F), (0x1AB0, 0x1ACE), (0x1B00, 0x1B03), (0x1B34, 0x1B34), (0x1B36, 0x1B3A),
        (0x1B3C, 0x1B3C), (0x1B42, 0x1B42), (0x1B6B, 0x1B73), (0x1B80, 0x1B81), (0x1BA2, 0x1BA5),
        (0x1BA8, 0x1BA9), (0x1BAB, 0x1BAD), (0x1BE6, 0x1BE6), (0x1BE8, 0x1BE9), (0x1BED, 0x1BED),
        (0x1BEF, 0x1BF1), (0x1C2C, 0x1C33), (0x1C36, 0x1C37), (0x1CD0, 0x1CD2), (0x1CD4, 0x1CE0),
        (0x1CE2, 0x1CE8), (0x1CED, 0x1CED), (0x1CF4, 0x1CF4), (0x1CF8, 0x1CF9), (0x1DC0, 0x1DFF),
        (0x20D0, 0x20F0), (0x2CEF, 0x2CF1), (0x2D7F, 0x2D7F), (0x2DE0, 0x2DFF), (0x302A, 0x302D),
        (0x3099, 0x309A), (0xA66F, 0xA672), (0xA674, 0xA67D), (0xA69E, 0xA69F), (0xA6F0, 0xA6F1),
        (0xA802, 0xA802), (0xA806, 0xA806), (0xA80B, 0xA80B), (0xA825, 0xA826), (0xA82C, 0xA82C),
        (0xA8C4, 0xA8C5), (0xA8E0, 0xA8F1), (0xA8FF, 0xA8FF), (0xA926, 0xA92D), (0xA947, 0xA951),
        (0xA980, 0xA982), (0xA9B3, 0xA9B3), (0xA9B6, 0xA9B9), (0xA9BC, 0xA9BD), (0xA9E5, 0xA9E5),
        (0xAA29, 0xAA2E), (0xAA31, 0xAA32), (0xAA35, 0xAA36), (0xAA43, 0xAA43), (0xAA4C, 0xAA4C),
        (0xAA7C, 0xAA7C), (0xAAB0, 0xAAB0), (0xAAB2, 0xAAB4), (0xAAB7, 0xAAB8), (0xAABE, 0xAABF),
        (0xAAC1, 0xAAC1), (0xAAEC, 0xAAED), (0xAAF6, 0xAAF6), (0xABE5, 0xABE5), (0xABE8, 0xABE8),
        (0xABED, 0xABED), (0xFB1E, 0xFB1E), (0xFE00, 0xFE0F), (0xFE20, 0xFE2F), (0x101FD, 0x101FD),
        (0x102E0, 0x102E0), (0x10376, 0x1037A), (0x10A01, 0x10A03), (0x10A05, 0x10A06), (0x10A0C, 0x10A0F),
        (0x10A38, 0x10A3A), (0x10A3F, 0x10A3F), (0x10AE5, 0x10AE6), (0x10D24, 0x10D27), (0x10D69, 0x10D6D),
        (0x10EAB, 0x10EAC), (0x10EFC, 0x10EFF), (0x10F46, 0x10F50), (0x10F82, 0x10F85), (0x11001, 0x11001),
        (0x11038, 0x11046), (0x11070, 0x11070), (0x11073, 0x11074), (0x1107F, 0x11081), (0x110B3, 0x110B6),
        (0x110B9, 0x110BA), (0x110C2, 0x110C2), (0x11100, 0x11102), (0x11127, 0x1112B), (0x1112D, 0x11134),
        (0x11173, 0x11173), (0x11180, 0x11181), (0x111B6, 0x111BE), (0x111C9, 0x111CC), (0x111CF, 0x111CF),
        (0x1122F, 0x11231), (0x11234, 0x11234), (0x11236, 0x11237), (0x1123E, 0x1123E), (0x11241, 0x11241),
        (0x112DF, 0x112DF), (0x112E3, 0x112EA), (0x11300, 0x11301), (0x1133B, 0x1133C), (0x11340, 0x11340),
        (0x11366, 0x1136C), (0x11370, 0x11374), (0x113BB, 0x113C0), (0x113CE, 0x113CE), (0x113D0, 0x113D0),
        (0x113D2, 0x113D2), (0x113E1, 0x113E2), (0x11438, 0x1143F), (0x11442, 0x11444), (0x11446, 0x11446),
        (0x1145E, 0x1145E), (0x114B3, 0x114B8), (0x114BA, 0x114BA), (0x114BF, 0x114C0), (0x114C2, 0x114C3),
        (0x115B2, 0x115B5), (0x115BC, 0x115BD), (0x115BF, 0x115C0), (0x115DC, 0x115DD), (0x11633, 0x1163A),
        (0x1163D, 0x1163D), (0x1163F, 0x11640), (0x116AB, 0x116AB), (0x116AD, 0x116AD), (0x116B0, 0x116B5),
        (0x116B7, 0x116B7), (0x1171D, 0x1171D), (0x1171F, 0x1171F), (0x11722, 0x11725), (0x11727, 0x1172B),
        (0x1182F, 0x11837), (0x11839, 0x1183A), (0x1193B, 0x1193C), (0x1193E, 0x1193E), (0x11943, 0x11943),
        (0x119D4, 0x119D7), (0x119DA, 0x119DB), (0x119E0, 0x119E0), (0x11A01, 0x11A0A), (0x11A33, 0x11A38),
        (0x11A3B, 0x11A3E), (0x11A47, 0x11A47), (0x11A51, 0x11A56), (0x11A59, 0x11A5B), (0x11A8A, 0x11A96),
        (0x11A98, 0x11A99), (0x11C30, 0x11C36), (0x11C38, 0x11C3D), (0x11C3F, 0x11C3F), (0x11C92, 0x11CA7),
        (0x11CAA, 0x11CB0), (0x11CB2, 0x11CB3), (0x11CB5, 0x11CB6), (0x11D31, 0x11D36), (0x11D3A, 0x11D3A),
        (0x11D3C, 0x11D3D), (0x11D3F, 0x11D45), (0x11D47, 0x11D47), (0x11D90, 0x11D91), (0x11D95, 0x11D95),
        (0x11D97, 0x11D97), (0x11EF3, 0x11EF4), (0x11F00, 0x11F01), (0x11F36, 0x11F3A), (0x11F40, 0x11F40),
        (0x11F42, 0x11F42), (0x11F5A, 0x11F5A), (0x13440, 0x13440), (0x13447, 0x13455), (0x1611E, 0x16129),
        (0x1612D, 0x1612F), (0x16AF0, 0x16AF4), (0x16B30, 0x16B36), (0x16F4F, 0x16F4F), (0x16F8F, 0x16F92),
        (0x16FE4, 0x16FE4), (0x1BC9D, 0x1BC9E), (0x1CF00, 0x1CF2D), (0x1CF30, 0x1CF46), (0x1D167, 0x1D169),
        (0x1D17B, 0x1D182), (0x1D185, 0x1D18B), (0x1D1AA, 0x1D1AD), (0x1D242, 0x1D244), (0x1DA00, 0x1DA36),
        (0x1DA3B, 0x1DA6C), (0x1DA75, 0x1DA75), (0x1DA84, 0x1DA84), (0x1DA9B, 0x1DA9F), (0x1DAA1, 0x1DAAF),
        (0x1E000, 0x1E006), (0x1E008, 0x1E018), (0x1E01B, 0x1E021), (0x1E023, 0x1E024), (0x1E026, 0x1E02A),
        (0x1E08F, 0x1E08F), (0x1E130, 0x1E136), (0x1E2AE, 0x1E2AE), (0x1E2EC, 0x1E2EF), (0x1E4EC, 0x1E4EF),
        (0x1E5EE, 0x1E5EF), (0x1E8D0, 0x1E8D6), (0x1E944, 0x1E94A), (0xE0100, 0xE01EF),
    ];
    // END generated combining marks

    /// <summary>The rule for combining marks as a text is read from its start: the first two marks on a character
    /// are drawn, and a third or later one, or a mark that starts the text, shows as its code. Two, because
    /// ordinary text needs two - a Thai vowel and its tone, decomposed Vietnamese, a Hebrew dagesh and
    /// vowel, an Arabic shadda and vowel. Even two tall marks reach past a line's own height, so the
    /// dialog clips each line of what would run to its own box, and no mark can draw over another line;
    /// the count keeps a tall stack, cut off by that clip, from hiding the line's own text.</summary>
    internal struct Marks
    {
        /// <summary>The most marks drawn on one character.</summary>
        internal const int Drawn = 2;
        /// <summary>Whether a character has been read yet: until one has, a mark has nothing to sit on.</summary>
        private bool started;
        /// <summary>The marks read since the last character that is not one.</summary>
        private int stacked;

        /// <summary>At a text's start, where a mark has nothing before it; or, <paramref name="atStart"/> false, just
        /// after a character that is not a mark.</summary>
        public Marks(bool atStart = true) => started = !atStart;

        /// <summary>Reads <paramref name="scalar"/>, the text's next, and says whether it is a mark that is not drawn,
        /// which shows as its code.</summary>
        public bool Bare(int scalar)
        {
            if (!IsCombiningMark(scalar))
            {
                started = true;
                stacked = 0;
                return false;
            }
            stacked++;
            return !started || stacked > Drawn;
        }
    }

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

    /// <summary>One argument of a command line: as written when it reads as one word - not empty, with no
    /// space, no ' and nothing <see cref="EscapedValue"/> escapes - else between single quotes, with \ and '
    /// escaped inside as well, so no two argument lists read alike.</summary>
    public static string Word(string text)
    {
        var scalars = Scalars(text).ToList();
        var reading = new Marks();
        if (scalars.Count > 0 && !scalars.Any(scalar => reading.Bare(scalar) || IsSpace(scalar) || scalar == Apostrophe || EscapeShown(scalar) is not null))
        {
            return text;
        }
        var word = new StringBuilder("'");
        var marks = new Marks();
        foreach (var scalar in scalars)
        {
            var bare = marks.Bare(scalar);
            word.Append(scalar switch
            {
                Backslash => "\\\\",
                Apostrophe => "\\'",
                _ => bare ? HexEscape(scalar) : EscapeShown(scalar) ?? Text(scalar),
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

    /// <summary>“a”, “a” and “b”, “a”, “b” and “c” - names as the app's sentences list them, each through
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

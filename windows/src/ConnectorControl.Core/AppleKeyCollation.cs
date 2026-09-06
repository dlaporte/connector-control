using System.Globalization;

namespace ConnectorControl.Core;

/// <summary>
/// Emulates the key order of Apple's JSONSerialization <c>.sortedKeys</c>: ICU root
/// collation with punctuation NOT ignorable (whitespace &lt; punctuation &lt; digits &lt;
/// letters), digit runs compared numerically, letters case-insensitive with a
/// lowercase-first tiebreak, ordinal as the final tiebreak.
/// </summary>
public static class AppleKeyCollation
{
    public static readonly IComparer<string> Comparer = new CollationComparer();

    private enum CharClass
    {
        Whitespace = 0,
        Punctuation = 1,
        Digit = 2,
        Letter = 3,
    }

    private sealed class CollationComparer : IComparer<string>
    {
        private static readonly CompareInfo Invariant = CultureInfo.InvariantCulture.CompareInfo;

        /// <summary>ICU root order for common ASCII punctuation and symbols; unranked characters sort after these, ordinally.</summary>
        private const string PunctuationRank = "_-,;:!?.'\"()[]{}@*/\\&#%`^+<=>|~$";

        public int Compare(string? x, string? y)
        {
            x ??= "";
            y ??= "";
            int c = CompareRuns(x, y, CompareOptions.IgnoreCase);
            if (c != 0)
            {
                return c;
            }
            c = CompareRuns(x, y, CompareOptions.None);   // ICU tertiary strength: lowercase first
            return c != 0 ? c : string.CompareOrdinal(x, y);
        }

        private static CharClass ClassOf(char ch)
        {
            if (char.IsWhiteSpace(ch)) { return CharClass.Whitespace; }
            if (char.IsAsciiDigit(ch)) { return CharClass.Digit; }
            if (char.IsPunctuation(ch) || char.IsSymbol(ch)) { return CharClass.Punctuation; }
            return CharClass.Letter;
        }

        private static int RunEnd(string s, int start)
        {
            var cls = ClassOf(s[start]);
            int end = start + 1;
            while (end < s.Length && ClassOf(s[end]) == cls) { end++; }
            return end;
        }

        /// <summary>Splits both strings into runs of one character class and compares run by run.</summary>
        private static int CompareRuns(string x, string y, CompareOptions letterOptions)
        {
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                var cx = ClassOf(x[i]);
                var cy = ClassOf(y[j]);
                if (cx != cy)
                {
                    return cx.CompareTo(cy);
                }
                int ei = RunEnd(x, i);
                int ej = RunEnd(y, j);
                var rx = x.AsSpan(i, ei - i);
                var ry = y.AsSpan(j, ej - j);
                int c = cx switch
                {
                    CharClass.Digit => CompareNumeric(rx, ry),
                    CharClass.Letter => Invariant.Compare(rx.ToString(), ry.ToString(), letterOptions),
                    CharClass.Punctuation => ComparePunctuation(rx, ry),
                    _ => rx.SequenceCompareTo(ry),
                };
                if (c != 0)
                {
                    return c;
                }
                i = ei;
                j = ej;
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }

        private static int CompareNumeric(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
        {
            a = a.TrimStart('0');
            b = b.TrimStart('0');
            if (a.Length != b.Length)
            {
                return a.Length.CompareTo(b.Length);
            }
            return a.SequenceCompareTo(b);
        }

        private static int ComparePunctuation(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int k = 0; k < n; k++)
            {
                if (a[k] == b[k]) { continue; }
                int ra = PunctuationRank.IndexOf(a[k]);
                int rb = PunctuationRank.IndexOf(b[k]);
                if (ra < 0 && rb < 0) { return a[k].CompareTo(b[k]); }
                if (ra < 0) { return 1; }
                if (rb < 0) { return -1; }
                return ra.CompareTo(rb);
            }
            return a.Length.CompareTo(b.Length);
        }
    }
}

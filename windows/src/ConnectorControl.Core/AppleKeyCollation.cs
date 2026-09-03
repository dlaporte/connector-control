using System.Globalization;

namespace ConnectorControl.Core;

/// <summary>
/// Emulates the key order of Apple's JSONSerialization <c>.sortedKeys</c>:
/// case-insensitive, digit runs compared numerically, lowercase before
/// uppercase on ties, ordinal as the final tiebreak.
/// </summary>
public static class AppleKeyCollation
{
    public static readonly IComparer<string> Comparer = new CollationComparer();

    private sealed class CollationComparer : IComparer<string>
    {
        private static readonly CompareInfo Invariant = CultureInfo.InvariantCulture.CompareInfo;

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

        /// <summary>Splits both strings into digit / non-digit runs and compares run by run.</summary>
        private static int CompareRuns(string x, string y, CompareOptions options)
        {
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                bool dx = char.IsAsciiDigit(x[i]);
                bool dy = char.IsAsciiDigit(y[j]);
                if (dx && dy)
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i])) { i++; }
                    while (j < y.Length && char.IsAsciiDigit(y[j])) { j++; }
                    var nx = x.AsSpan(si, i - si).TrimStart('0');
                    var ny = y.AsSpan(sj, j - sj).TrimStart('0');
                    if (nx.Length != ny.Length)
                    {
                        return nx.Length.CompareTo(ny.Length);
                    }
                    int cn = nx.SequenceCompareTo(ny);
                    if (cn != 0)
                    {
                        return cn;
                    }
                    continue;
                }
                if (dx != dy)
                {
                    int cc = Invariant.Compare(x.Substring(i, 1), y.Substring(j, 1), options);
                    if (cc != 0)
                    {
                        return cc;
                    }
                    i++;
                    j++;
                    continue;
                }
                int ti = i, tj = j;
                while (i < x.Length && !char.IsAsciiDigit(x[i])) { i++; }
                while (j < y.Length && !char.IsAsciiDigit(y[j])) { j++; }
                int cr = Invariant.Compare(x.Substring(ti, i - ti), y.Substring(tj, j - tj), options);
                if (cr != 0)
                {
                    return cr;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}

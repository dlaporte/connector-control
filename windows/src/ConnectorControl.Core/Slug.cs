using System.Text;

namespace ConnectorControl.Core;

/// <summary>
/// File names for collection documents: lowercase ASCII letters and digits, every other run
/// collapsed to one hyphen, so a Mac, a PC and a case-sensitive git server agree on the name.
/// </summary>
public static class Slug
{
    public static string Make(string name)
    {
        var sb = new StringBuilder();
        var pendingHyphen = false;
        // The invariant lowercase leaves "İ" as it is, so "İstanbul" slugs as "stanbul", where the
        // Mac, lowering the whole string, expands it to "i" and a combining dot and writes
        // "i-stanbul". Harmless: the slug is fixed once, in the sidecar, by whichever machine
        // publishes first, and never derived again.
        foreach (var ch in name.ToLowerInvariant())
        {
            var isAlnum = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9');
            if (isAlnum)
            {
                if (pendingHyphen && sb.Length > 0)
                {
                    sb.Append('-');
                }
                pendingHyphen = false;
                sb.Append(ch);
            }
            else
            {
                pendingHyphen = true;
            }
        }
        return sb.Length == 0 ? "collection" : sb.ToString();
    }
}

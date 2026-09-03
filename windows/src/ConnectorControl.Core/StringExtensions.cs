using System.Globalization;

namespace ConnectorControl.Core;

public static class StringExtensions
{
    /// <summary>
    /// Swift's <c>trimmingCharacters(in: .whitespaces)</c>: strips Unicode
    /// category Zs and TAB from both ends — but NOT newlines.
    /// </summary>
    public static string TrimSpaces(this string s)
    {
        static bool IsSpace(char c) =>
            c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

        int start = 0;
        int end = s.Length;
        while (start < end && IsSpace(s[start]))
        {
            start++;
        }
        while (end > start && IsSpace(s[end - 1]))
        {
            end--;
        }
        return s[start..end];
    }
}

using System.Globalization;

namespace ConnectorControl.Core;

public static class BackupTimestamp
{
    /// <summary>
    /// UTC, not local time: backup recency is a lexicographic sort of these
    /// stamps, and local wall-clock repeats an hour every DST fall-back.
    /// </summary>
    public static string From(DateTime date)
    {
        // Callers pass UTC; an Unspecified Kind must not be reinterpreted as local time
        // by ToUniversalTime(), which would shift the stamp by the local UTC offset.
        if (date.Kind == DateTimeKind.Unspecified) { date = DateTime.SpecifyKind(date, DateTimeKind.Utc); }
        return date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH-mm-ss-fff'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>The instant a stamp names, in UTC, or null for text that is not one.</summary>
    public static DateTime? Parse(string stamp) =>
        DateTime.TryParseExact(stamp, "yyyy-MM-dd'T'HH-mm-ss-fff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
}

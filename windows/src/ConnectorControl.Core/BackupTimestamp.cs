using System.Globalization;

namespace ConnectorControl.Core;

public static class BackupTimestamp
{
    /// <summary>
    /// UTC, not local time: backup recency is a lexicographic sort of these
    /// stamps, and local wall-clock repeats an hour every DST fall-back.
    /// </summary>
    public static string From(DateTime date) =>
        date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH-mm-ss-fff'Z'", CultureInfo.InvariantCulture);
}

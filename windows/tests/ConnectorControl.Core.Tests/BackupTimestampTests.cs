namespace ConnectorControl.Core.Tests;

public class BackupTimestampTests
{
    [Fact]
    public void BackupTimestampsSortChronologicallyAcrossDstFallBack()
    {
        // 2026-11-01 America/New_York repeats 01:00–02:00; UTC stamps must still increase.
        var start = DateTime.UnixEpoch.AddSeconds(1_793_500_000);
        var previous = "";
        for (int step = 0; step < 10; step++)
        {
            var stamp = BackupTimestamp.From(start.AddSeconds(step * 1800));
            Assert.True(string.CompareOrdinal(stamp, previous) > 0, $"{stamp} <= {previous}");
            previous = stamp;
        }
    }

    [Fact]
    public void BackupTimestampFormat()
    {
        Assert.Equal("2025-07-15T17-20-00-123Z", BackupTimestamp.From(DateTime.UnixEpoch.AddSeconds(1_752_600_000).AddMilliseconds(123)));
    }

    [Fact]
    public void BackupTimestampTreatsUnspecifiedKindAsUtc()
    {
        var unspecified = new DateTime(2025, 7, 15, 17, 20, 0, DateTimeKind.Unspecified);
        Assert.Equal("2025-07-15T17-20-00-000Z", BackupTimestamp.From(unspecified));
    }
}

namespace ConnectorControl.Core.Tests;

/// <summary>Tests/ConnectorControlCoreTests/IsoTimestampTests.swift</summary>
public class IsoTimestampTests
{
    [Fact]
    public void AnInstantIsWrittenAsIso8601UtcToTheSecond()
    {
        Assert.Equal("2026-09-21T14:02:11Z", IsoTimestamp.String(new DateTime(2026, 9, 21, 14, 2, 11, DateTimeKind.Utc)));
        // Every field is zero-padded, and a fractional second is dropped rather than rounded up.
        Assert.Equal("2026-01-01T00:00:05Z",
            IsoTimestamp.String(new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc).AddMilliseconds(750)));
    }
}

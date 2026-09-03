using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

public class BackupManagerTests : IDisposable
{
    private const string Series = "claude_desktop_config";
    private readonly TempDir dir = new("backups");
    private readonly string source;
    private readonly BackupManager manager;

    public BackupManagerTests()
    {
        source = dir.File("claude_desktop_config.json");
        File.WriteAllText(source, "{\"mcpServers\": {}}");
        manager = new BackupManager(dir.File("backups"), keepCount: 3);
    }

    public void Dispose() => dir.Dispose();

    private static DateTime At(double unixSeconds) => DateTime.UnixEpoch.AddSeconds(unixSeconds);

    [Fact]
    public void BackUpCreatesTimestampedCopy()
    {
        var made = manager.BackUp(source, Series, At(1_752_600_000));
        Assert.NotNull(made);
        Assert.StartsWith("claude_desktop_config.", Path.GetFileName(made), StringComparison.Ordinal);
        Assert.EndsWith(".json", made, StringComparison.Ordinal);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(made));
    }

    [Fact]
    public void BackUpSkipsWhenIdenticalToNewest()
    {
        var first = manager.BackUp(source, Series, At(1_752_600_000));
        var second = manager.BackUp(source, Series, At(1_752_600_001));
        Assert.Equal(first, second);   // identical content returns the existing newest backup
        Assert.Single(manager.Backups(Series));
        File.WriteAllText(source, "changed");
        var third = manager.BackUp(source, Series, At(1_752_600_002));
        Assert.NotEqual(first, third);
        Assert.Equal(2, manager.Backups(Series).Count);
    }

    [Fact]
    public void BackUpDedupsOnlyAgainstNewest()
    {
        string[] contents = ["A", "B", "A"];
        for (int i = 0; i < contents.Length; i++)
        {
            File.WriteAllText(source, contents[i]);
            manager.BackUp(source, Series, At(1_752_600_000 + i));
        }
        Assert.Equal(3, manager.Backups(Series).Count);
    }

    [Fact]
    public void BackUpMissingSourceReturnsNull()
    {
        Assert.Null(manager.BackUp(dir.File("nope.json"), Series));
    }

    [Fact]
    public void RotationKeepsNewestKeepCount()
    {
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(source, $"v{i}");
            manager.BackUp(source, Series, At(1_752_600_000 + i));
        }
        var kept = manager.Backups(Series);
        Assert.Equal(3, kept.Count);
        Assert.Equal("v4", File.ReadAllText(kept[0]));
        Assert.Equal("v2", File.ReadAllText(kept[2]));
    }

    [Fact]
    public void OriginalSnapshotWrittenOnceAndNeverPruned()
    {
        manager.EnsureOriginalSnapshot(source);
        File.WriteAllText(source, "changed");
        manager.EnsureOriginalSnapshot(source);   // second call: no-op
        var original = Path.Combine(manager.BackupsDir, "claude_desktop_config.original.json");
        Assert.Equal("{\"mcpServers\": {}}", File.ReadAllText(original));
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(source, $"w{i}");
            manager.BackUp(source, Series, At(1_752_700_000 + i));
        }
        Assert.True(File.Exists(original));
        Assert.DoesNotContain(manager.Backups(Series), p => Path.GetFileName(p).Contains(".original.", StringComparison.Ordinal));
    }

    [Fact]
    public void SameMillisecondBackupsBothSucceed()
    {
        var now = At(1_752_600_000.123);
        File.WriteAllText(source, "v0");
        var first = manager.BackUp(source, Series, now);
        File.WriteAllText(source, "v1");
        var second = manager.BackUp(source, Series, now);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal(2, manager.Backups(Series).Count);
    }

    [Fact]
    public void BackupsArePrivate()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;   // CA1416: the analyzer needs an explicit exit after the guard
        }
        var made = manager.BackUp(source, "mcps");
        Assert.NotNull(made);
        Assert.True(OwnerOnlyAcl.IsOwnerOnly(made));
    }

    [Fact]
    public void SeriesAreIndependent()
    {
        manager.BackUp(source, Series);
        manager.BackUp(source, "mcps");
        Assert.Single(manager.Backups(Series));
        Assert.Single(manager.Backups("mcps"));
    }

    [Fact]
    public void BackupsOfMissingDirIsEmpty()
    {
        Assert.Empty(new BackupManager(dir.File("never")).Backups(Series));
    }
}

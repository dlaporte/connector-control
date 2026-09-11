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

    /// <summary>Pruning is best effort: a stale backup that refuses deletion must not fail the write it trails.</summary>
    [Fact]
    public void RotationSkipsAStaleBackupThatCannotBeDeleted()
    {
        // A locked file blocks File.Delete on Windows; on Unix, unlink succeeds on an open
        // file, so this scenario cannot be forced there.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;   // CA1416: the analyzer needs an explicit exit after the guard
        }
        File.WriteAllText(source, "v0");
        var oldest = manager.BackUp(source, Series, At(1_752_600_000));
        Assert.NotNull(oldest);
        using var locked = new FileStream(oldest!, FileMode.Open, FileAccess.Read, FileShare.Read);
        for (int i = 1; i <= 3; i++)
        {
            File.WriteAllText(source, $"v{i}");
            manager.BackUp(source, Series, At(1_752_600_000 + i));   // must not throw even though pruning oldest fails
        }
        Assert.True(File.Exists(oldest));                 // the locked backup survives the failed prune
        Assert.Equal(4, manager.Backups(Series).Count);   // one more than KeepCount, because the oldest refused deletion
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
    public void BackupsDirectoryAndOriginalSnapshotArePrivate()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;   // CA1416: the analyzer needs an explicit exit after the guard
        }
        manager.EnsureOriginalSnapshot(source);
        Assert.True(OwnerOnlyAcl.IsOwnerOnly(manager.BackupsDir));
        Assert.True(OwnerOnlyAcl.IsOwnerOnly(Path.Combine(manager.BackupsDir, "claude_desktop_config.original.json")));
    }

    /// <summary>A config symlinked into a dotfiles repo: the backup must be a snapshot of the bytes,
    /// not a copy of the link (which would read the live file forever, so no restore could ever go back).</summary>
    [Fact]
    public void BackupOfASymlinkedSourceIsARealSnapshot()
    {
        var real = dir.File(Path.Combine("dotfiles", "claude.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        File.WriteAllText(real, "v1");
        var link = dir.File("linked_config.json");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Creating a symlink needs Developer Mode or elevation; GitHub's hosted Windows runners are elevated, so under FailSkips=true (ci.runsettings) this skip would surface as a failure if that ever changed.
            Assert.Skip("symlink creation is not permitted in this environment");
            return;
        }
        var made = manager.BackUp(link, Series);
        Assert.NotNull(made);
        Assert.Null(new FileInfo(made!).LinkTarget);   // the backup is a regular file, not a link
        File.WriteAllText(real, "v2");
        Assert.Equal("v1", File.ReadAllText(made!));   // the snapshot does not follow the live file
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

    [Fact]
    public void AFreshBackupsTreeIsOwnerOnlyFromTheParentDown()
    {
        // First run with an empty Claude config: the backups dir is the first thing the app creates,
        // so its parent (the default store dir) must be private from the create call too.
        var backups = dir.File(Path.Combine("Connector Control", "backups"));
        var fresh = new BackupManager(backups, keepCount: 3);
        var made = fresh.BackUp(source, Series, At(1_752_600_000));
        Assert.NotNull(made);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(made));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(Path.GetDirectoryName(backups)!), "the parent this call created is private");
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(backups));
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(made), "the copy is private from its create call, not a fix-up");
        }
    }
}

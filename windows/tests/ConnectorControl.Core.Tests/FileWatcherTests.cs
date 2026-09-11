using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

public class FileWatcherTests : IDisposable
{
    private readonly TempDir dir = new("watch");
    private readonly string path;

    public FileWatcherTests()
    {
        path = dir.File("watched.json");
    }

    public void Dispose() => dir.Dispose();

    /// <summary>Counts callbacks; "marshal" runs inline so the test thread sees them.</summary>
    private sealed class Counter
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public void Hit() => Interlocked.Increment(ref count);
    }

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Gives a just-started FileSystemWatcher's own background thread a moment to
    /// finish arming before a test relies on it observing the very next change.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    [Fact]
    public void FiresOnInPlaceWrite()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit);
        watcher.Start();
        Assert.True(watcher.IsArmed);
        TempDir.Touch(path, "two");
        Assert.True(Wait.Until(() => counter.Count >= 1, WaitTimeout), "expected a change callback after an in-place write");
    }

    [Fact]
    public void FiresOnAtomicReplace()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit);
        watcher.Start();
        AtomicFile.Write("two"u8.ToArray(), path);
        TempDir.BumpModificationTime(path);   // the write's own mtime may tie with "one"'s on a coarse file system
        Assert.True(Wait.Until(() => counter.Count >= 1, WaitTimeout), "expected a change callback after an atomic replace");
    }

    [Fact]
    public void FiresOnDeleteAndOnRecreate()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit);
        watcher.Start();
        Thread.Sleep(Settle);
        File.Delete(path);
        Assert.True(Wait.Until(() => counter.Count >= 1, WaitTimeout), "expected a callback for delete");
        Thread.Sleep(Settle);
        File.WriteAllText(path, "again");
        Assert.True(Wait.Until(() => counter.Count >= 2, WaitTimeout), "expected a callback for recreate");
    }

    [Fact]
    public void DoesNotFireAfterStop()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit);
        watcher.Start();
        watcher.Stop();
        File.WriteAllText(path, "two");
        Thread.Sleep(1500);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public void DoesNotFireWhenStoppedWhileAnEventIsInFlight()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit, debounce: TimeSpan.FromMilliseconds(500));
        watcher.Start();
        File.WriteAllText(path, "two");   // the debounce timer is now armed
        Thread.Sleep(100);
        watcher.Stop();                   // before the 500 ms debounce elapses
        Thread.Sleep(1500);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public void RestartDropsCallbacksScheduledBeforeTheRestart()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit, debounce: TimeSpan.FromMilliseconds(500));
        watcher.Start();
        Thread.Sleep(Settle);
        TempDir.Touch(path, "two");
        Thread.Sleep(100);
        watcher.Stop();
        watcher.Start();                  // new generation; the old timer's work must not leak through
        Thread.Sleep(1500);
        Assert.Equal(0, counter.Count);
        File.WriteAllText(path, "three"); // the re-armed watcher still works
        Assert.True(Wait.Until(() => counter.Count >= 1, WaitTimeout));
    }

    [Fact]
    public void CoalescesBurstsIntoOneCallback()
    {
        File.WriteAllText(path, "0");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit, debounce: TimeSpan.FromMilliseconds(400));
        watcher.Start();
        Thread.Sleep(Settle);
        for (int i = 1; i <= 5; i++)
        {
            File.WriteAllText(path, i.ToString());
            Thread.Sleep(20);
        }
        TempDir.BumpModificationTime(path);   // guarantee the burst's final mtime differs from "0"'s on a coarse file system
        Assert.True(Wait.Until(() => counter.Count >= 1, WaitTimeout));
        Thread.Sleep(1500);
        Assert.True(counter.Count <= 2, $"expected the burst to coalesce, got {counter.Count} callbacks");
    }

    [Fact]
    public void IgnoresOtherFilesInTheDirectory()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit);
        watcher.Start();
        Thread.Sleep(Settle);
        File.WriteAllText(dir.File("other.json"), "x");
        Thread.Sleep(1500);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public void StaysUnarmedUntilTheParentDirectoryExists()
    {
        var nested = dir.File(System.IO.Path.Combine("later", "file.json"));
        var counter = new Counter();
        using var watcher = new FileWatcher(nested, a => a(), counter.Hit);
        watcher.Start();
        Assert.False(watcher.IsArmed);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(nested)!);
        watcher.Start();   // re-arm attempt, as AppState does on each reload
        Assert.True(watcher.IsArmed);
        Thread.Sleep(Settle);
        File.WriteAllText(nested, "hello");
        Assert.True(Wait.Until(() => counter.Count >= 1, WaitTimeout));
    }

    /// <summary>
    /// Drives HandleError's "directory is gone" branch entirely through the internal
    /// seam (no Start(), no live FileSystemWatcher at all). That branch is pure
    /// internal logic — Stop() plus exactly one marshalled callback — and doesn't
    /// need a real OS watcher to prove it. This used to be folded into the
    /// real-file-system test below, alongside a genuinely-armed watcher and a real
    /// Directory.Delete; that combination was flaky on Windows CI (never on the
    /// Mac): once Start() has wired up a live FileSystemWatcher, the OS can
    /// independently raise its own Error event for the same deletion on a
    /// background thread, racing this manual call and occasionally delivering the
    /// callback twice before the very next assertion ran. Never arming a real
    /// watcher here removes that race entirely, so the "exactly once" guarantee
    /// (R2: the deletion must be delivered, not swallowed by Stop()) can be
    /// asserted deterministically. The real-armed-watcher scenario (Stop() tearing
    /// down a genuinely live FileSystemWatcher) is still covered by DoesNotFireAfterStop
    /// and RestartDropsCallbacksScheduledBeforeTheRestart above, and by the
    /// tolerant, real-file-system test below.
    /// </summary>
    [Fact]
    public void HandleErrorWithTheDirectoryGoneDisarmsAndDeliversExactlyOnce()
    {
        var missing = dir.File(System.IO.Path.Combine("gone", "watched.json"));
        var counter = new Counter();
        using var watcher = new FileWatcher(missing, a => a(), counter.Hit);
        Assert.False(watcher.IsArmed);
        watcher.HandleError();      // what the FileSystemWatcher raises when its directory goes
        Assert.False(watcher.IsArmed);
        Assert.Equal(1, counter.Count);   // the deletion itself must be delivered, not swallowed by Stop() (R2)
    }

    /// <summary>Retries past the Windows quirk where deleting a directory a live
    /// FileSystemWatcher still holds a handle on can transiently fail/need a retry.</summary>
    private static void DeleteDirectoryWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
        }
    }

    [Fact]
    public void ADeletedDirectoryDisarmsTheWatcherSoTheNextStartReArms()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit);
        watcher.Start();
        Assert.True(watcher.IsArmed);
        var parent = System.IO.Path.GetDirectoryName(path)!;
        DeleteDirectoryWithRetry(parent);
        watcher.HandleError();      // what the FileSystemWatcher raises when its directory goes
        Assert.False(watcher.IsArmed);
        // The real, now-disposed FileSystemWatcher can independently raise its own
        // Error event for this same deletion on a background thread (the Windows CI
        // flake this test used to hit): tolerate a possible extra delivery here with
        // a condition-based wait rather than an exact synchronous count.
        // HandleErrorWithTheDirectoryGoneDisarmsAndDeliversExactlyOnce above is the
        // deterministic proof that exactly one callback fires.
        Assert.True(Wait.Until(() => counter.Count >= 1, WaitTimeout), "expected the deletion to be delivered, not swallowed by Stop() (R2)");
        watcher.Start();            // AppState retries on each reload; the directory is still gone
        Assert.False(watcher.IsArmed);
        Directory.CreateDirectory(parent);
        watcher.Start();
        Assert.True(watcher.IsArmed);
        Thread.Sleep(Settle);
        var before = counter.Count;   // a possible earlier extra delivery must not mask a missing one here
        File.WriteAllText(path, "two");
        Assert.True(Wait.Until(() => counter.Count >= before + 1, WaitTimeout), "the re-armed watcher must still report changes");
    }

    [Fact]
    public void AnErrorWithTheDirectoryStillThereKeepsTheWatcherArmed()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit);
        watcher.Start();
        watcher.HandleError();      // a buffer overflow: re-check, but stay armed
        Assert.True(watcher.IsArmed);
        TempDir.Touch(path, "two");
        Assert.True(Wait.Until(() => counter.Count >= 1, WaitTimeout));
    }

    [Fact]
    public void CallbackGoesThroughMarshal()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        var marshalled = 0;
        using var watcher = new FileWatcher(path, a => { Interlocked.Increment(ref marshalled); a(); }, counter.Hit);
        watcher.Start();
        TempDir.Touch(path, "two");
        Assert.True(Wait.Until(() => counter.Count >= 1, WaitTimeout));
        Assert.True(Volatile.Read(ref marshalled) >= 1);
    }
}

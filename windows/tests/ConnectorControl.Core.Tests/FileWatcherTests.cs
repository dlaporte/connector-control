using ConnectorControl.Core.Services;
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
        public bool WaitFor(int expected, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (Count >= expected) { return true; }
                Thread.Sleep(50);
            }
            return Count >= expected;
        }
    }

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);

    [Fact]
    public void FiresOnInPlaceWrite()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, counter.Hit, a => a());
        watcher.Start();
        Assert.True(watcher.IsArmed);
        Thread.Sleep(1100);   // mtime resolution on some file systems is 1 s
        File.WriteAllText(path, "two");
        Assert.True(counter.WaitFor(1, Wait), "expected a change callback after an in-place write");
    }

    [Fact]
    public void FiresOnAtomicReplace()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, counter.Hit, a => a());
        watcher.Start();
        Thread.Sleep(1100);
        AtomicFile.Write("two"u8.ToArray(), path);
        Assert.True(counter.WaitFor(1, Wait), "expected a change callback after an atomic replace");
    }

    [Fact]
    public void FiresOnDeleteAndOnRecreate()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, counter.Hit, a => a());
        watcher.Start();
        Thread.Sleep(300);
        File.Delete(path);
        Assert.True(counter.WaitFor(1, Wait), "expected a callback for delete");
        Thread.Sleep(300);
        File.WriteAllText(path, "again");
        Assert.True(counter.WaitFor(2, Wait), "expected a callback for recreate");
    }

    [Fact]
    public void DoesNotFireAfterStop()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, counter.Hit, a => a());
        watcher.Start();
        watcher.Stop();
        Thread.Sleep(1100);
        File.WriteAllText(path, "two");
        Thread.Sleep(1500);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public void DoesNotFireWhenStoppedWhileAnEventIsInFlight()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, counter.Hit, a => a(), debounce: TimeSpan.FromMilliseconds(500));
        watcher.Start();
        Thread.Sleep(1100);
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
        using var watcher = new FileWatcher(path, counter.Hit, a => a(), debounce: TimeSpan.FromMilliseconds(500));
        watcher.Start();
        Thread.Sleep(1100);
        File.WriteAllText(path, "two");
        Thread.Sleep(100);
        watcher.Stop();
        watcher.Start();                  // new generation; the old timer's work must not leak through
        Thread.Sleep(1500);
        Assert.Equal(0, counter.Count);
        File.WriteAllText(path, "three"); // the re-armed watcher still works
        Assert.True(counter.WaitFor(1, Wait));
    }

    [Fact]
    public void CoalescesBurstsIntoOneCallback()
    {
        File.WriteAllText(path, "0");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, counter.Hit, a => a(), debounce: TimeSpan.FromMilliseconds(400));
        watcher.Start();
        Thread.Sleep(1100);
        for (int i = 1; i <= 5; i++)
        {
            File.WriteAllText(path, i.ToString());
            Thread.Sleep(20);
        }
        Assert.True(counter.WaitFor(1, Wait));
        Thread.Sleep(1500);
        Assert.True(counter.Count <= 2, $"expected the burst to coalesce, got {counter.Count} callbacks");
    }

    [Fact]
    public void IgnoresOtherFilesInTheDirectory()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, counter.Hit, a => a());
        watcher.Start();
        Thread.Sleep(300);
        File.WriteAllText(dir.File("other.json"), "x");
        Thread.Sleep(1500);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public void StaysUnarmedUntilTheParentDirectoryExists()
    {
        var nested = dir.File(System.IO.Path.Combine("later", "file.json"));
        var counter = new Counter();
        using var watcher = new FileWatcher(nested, counter.Hit, a => a());
        watcher.Start();
        Assert.False(watcher.IsArmed);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(nested)!);
        watcher.Start();   // re-arm attempt, as AppState does on each reload
        Assert.True(watcher.IsArmed);
        Thread.Sleep(300);
        File.WriteAllText(nested, "hello");
        Assert.True(counter.WaitFor(1, Wait));
    }

    [Fact]
    public void ADeletedDirectoryDisarmsTheWatcherSoTheNextStartReArms()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, counter.Hit, a => a());
        watcher.Start();
        Assert.True(watcher.IsArmed);
        var parent = System.IO.Path.GetDirectoryName(path)!;
        Directory.Delete(parent, recursive: true);
        watcher.HandleError();      // what the FileSystemWatcher raises when its directory goes
        Assert.False(watcher.IsArmed);
        Assert.Equal(1, counter.Count);   // the deletion itself must be delivered, not swallowed by Stop() (R2)
        watcher.Start();            // AppState retries on each reload; the directory is still gone
        Assert.False(watcher.IsArmed);
        Directory.CreateDirectory(parent);
        watcher.Start();
        Assert.True(watcher.IsArmed);
        Thread.Sleep(300);
        File.WriteAllText(path, "two");
        Assert.True(counter.WaitFor(2, Wait), "the re-armed watcher must still report changes");
    }

    [Fact]
    public void AnErrorWithTheDirectoryStillThereKeepsTheWatcherArmed()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, counter.Hit, a => a());
        watcher.Start();
        watcher.HandleError();      // a buffer overflow: re-check, but stay armed
        Assert.True(watcher.IsArmed);
        Thread.Sleep(1100);
        File.WriteAllText(path, "two");
        Assert.True(counter.WaitFor(1, Wait));
    }

    [Fact]
    public void CallbackGoesThroughMarshal()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        var marshalled = 0;
        using var watcher = new FileWatcher(path, counter.Hit, a => { Interlocked.Increment(ref marshalled); a(); });
        watcher.Start();
        Thread.Sleep(1100);
        File.WriteAllText(path, "two");
        Assert.True(counter.WaitFor(1, Wait));
        Assert.True(Volatile.Read(ref marshalled) >= 1);
    }
}

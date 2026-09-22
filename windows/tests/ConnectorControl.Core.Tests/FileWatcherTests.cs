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

    /// <summary>
    /// The rebuild that keeps a replaced folder from being watched by a dead handle. Whatever
    /// raised the error, the FileSystemWatcher behind it is spent, so a fresh one takes its
    /// place — proved here by the arm count, which is a fact about this object and so does not
    /// depend on what a platform's FileSystemWatcher does with a handle on a folder that has
    /// been deleted. Nothing happens to the directory in this test, so the OS cannot raise an
    /// error of its own to race the count.
    /// </summary>
    [Fact]
    public void AnErrorWithTheDirectoryStillThereRebuildsTheFileSystemWatcher()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit);
        watcher.Start();
        var armed = watcher.ArmCount;
        watcher.HandleError();
        Assert.True(watcher.IsArmed);
        Assert.Equal(armed + 1, watcher.ArmCount);
        Thread.Sleep(Settle);
        var before = counter.Count;   // the error's own re-check may already have reported
        TempDir.Touch(path, "two");
        Assert.True(Wait.Until(() => counter.Count >= before + 1, WaitTimeout), "the rebuilt FileSystemWatcher must still report changes");
    }

    /// <summary>
    /// A FileSystemWatcher watches the folder it was armed on, not the path: a folder replaced
    /// wholesale leaves it holding one that is no longer there, where it is silent for good. The
    /// watcher's only handle on folder identity is the creation time it armed on, so changing
    /// that is exactly the input a replacement produces — and it is the one way to produce it
    /// without deleting or moving anything, which would let the OS raise an error of its own and
    /// race these assertions. IsArmed must then say no, and the re-arm every reload performs
    /// must swap the watcher onto the folder that is there.
    /// </summary>
    [Fact]
    public void AFolderThatIsNoLongerTheOneArmedOnIsNotReportedAsArmed()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit);
        watcher.Start();
        Assert.True(watcher.IsArmed);
        Directory.SetCreationTimeUtc(dir.Path, Directory.GetCreationTimeUtc(dir.Path).AddHours(-1));
        Assert.False(watcher.IsArmed, "the folder at the path is not the one being watched");
        watcher.Start();   // AppState re-arms on every reload: this is where recovery happens
        Assert.True(watcher.IsArmed);
        Thread.Sleep(Settle);
        var before = counter.Count;
        TempDir.Touch(path, "two");
        Assert.True(Wait.Until(() => counter.Count >= before + 1, WaitTimeout), "the re-armed watcher must still report changes");
    }

    /// <summary>
    /// The real shape of the above: a sync client renames the shared folder away and puts
    /// another copy at the path. A rename raises no error on Windows — the handle follows the
    /// folder it was opened on — so nothing but the next Start() can notice, which is why
    /// IsArmed has to tell the truth. The creation time of the replacement is set explicitly
    /// because NTFS hands a name recreated in the same parent within about fifteen seconds its
    /// predecessor's creation time, and without that line this test would quietly stop
    /// exercising the identity check on Windows; a folder a sync client puts there minutes
    /// later brings its own. The arm count is what gives the test teeth on both platforms: on
    /// the Mac the runtime's watcher follows the path anyway, so the reporting at the end
    /// would pass with or without the swap.
    /// </summary>
    [Fact]
    public void AWatchedFolderRenamedAwayAndReplacedIsFollowedOnTheNextStart()
    {
        var folder = dir.File("collection");
        Directory.CreateDirectory(folder);
        var file = System.IO.Path.Combine(folder, "watched.json");
        File.WriteAllText(file, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(file, a => a(), counter.Hit);
        watcher.Start();
        Assert.True(watcher.IsArmed);
        var armed = watcher.ArmCount;
        Thread.Sleep(Settle);

        Directory.Move(folder, dir.File("collection-renamed-away"));
        Directory.CreateDirectory(folder);
        Directory.SetCreationTimeUtc(folder, DateTime.UtcNow.AddHours(-1));

        watcher.Start();
        Assert.True(watcher.IsArmed);
        Assert.Equal(armed + 1, watcher.ArmCount);   // it swapped onto the folder that is there
        // The file is not in the replacement, and the re-arm's own re-check reports that, so
        // the count is settled before the write below rather than racing it.
        Assert.True(Wait.Until(() => counter.Count >= 1, WaitTimeout), "the re-arm must report the file missing from the folder that is there now");
        var before = counter.Count;
        TempDir.Touch(file, "two");   // creates the file in the folder that is there now
        Assert.True(Wait.Until(() => counter.Count >= before + 1, WaitTimeout), "the watcher must follow the path, not the folder it happened to open");
    }

    /// <summary>
    /// The re-arm has to re-check, not only re-arm: a folder put at the path with a file that
    /// already differs will never fire for a change that happened before the new watcher
    /// existed, so without the re-check that change waits for an unrelated write. The path
    /// probe stands in for the replacement's contents, which is what makes this deterministic
    /// on any platform — no real write happens, so no FileSystemWatcher event of any kind can
    /// report the change for the wrong reason.
    /// </summary>
    [Fact]
    public void TheReArmOnAReplacedFolderReportsAChangeAlreadyWaitingInIt()
    {
        var probe = new FakePathProbe().AddFile(path, DateTime.UnixEpoch);
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit, probe: probe);
        watcher.Start();
        probe.AddFile(path, DateTime.UnixEpoch.AddHours(1));
        Directory.SetCreationTimeUtc(dir.Path, Directory.GetCreationTimeUtc(dir.Path).AddHours(-1));
        Assert.False(watcher.IsArmed);

        watcher.Start();

        Assert.True(Wait.Until(() => counter.Count >= 1, WaitTimeout), "the re-arm must re-check the file, not wait for the next write to it");
    }

    /// <summary>
    /// A lost buffer says nothing about which folder the handle is on, and a folder syncing a
    /// large batch can overflow the buffer again and again, so an overflow re-checks and keeps
    /// the watcher it has.
    /// </summary>
    [Fact]
    public void ALostBufferRechecksWithoutRebuilding()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit);
        watcher.Start();
        var armed = watcher.ArmCount;
        watcher.HandleError(rebuild: false);   // what OnError passes for an InternalBufferOverflowException
        Assert.True(watcher.IsArmed);
        Assert.Equal(armed, watcher.ArmCount);
        TempDir.Touch(path, "two");
        Assert.True(Wait.Until(() => counter.Count >= 1, WaitTimeout), "the re-check still reports");
    }

    [Fact]
    public void OnlyAnErrorThatCouldMeanTheWrongFolderWarrantsARebuild()
    {
        Assert.False(FileWatcher.WarrantsRebuild(new InternalBufferOverflowException()));
        Assert.True(FileWatcher.WarrantsRebuild(new IOException("the watched directory is gone")));
        Assert.True(FileWatcher.WarrantsRebuild(null));
    }

    /// <summary>
    /// Errors arrive in storms, and each rebuild costs a directory handle and a window in which
    /// events are missed, so they are bounded to one per cooldown; the rest are owed, and the
    /// next Start() pays them with a single swap. The cooldown is injected rather than waited
    /// out, which is what makes the counts exact instead of a race with the clock.
    /// </summary>
    [Fact]
    public void AStormOfErrorsCostsOneRebuild()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit, rebuildCooldown: TimeSpan.FromMinutes(5));
        watcher.Start();
        var armed = watcher.ArmCount;
        for (var i = 0; i < 25; i++)
        {
            watcher.HandleError();
        }
        Assert.Equal(armed + 1, watcher.ArmCount);
        Assert.False(watcher.IsArmed, "the errors the cooldown held back are owed a rebuild");
        TempDir.Touch(path, "two");
        Assert.True(Wait.Until(() => counter.Count >= 1, WaitTimeout), "the watcher it kept still reports changes meanwhile");
        watcher.Start();
        Assert.Equal(armed + 2, watcher.ArmCount);   // the whole storm's debt, paid once
        Assert.True(watcher.IsArmed);
    }

    /// <summary>
    /// A rebuild the cooldown holds back is owed, not dropped. If the error it answered came from
    /// a dead handle — a folder deleted and recreated under the same name, which keeps its
    /// creation time on NTFS, so the identity check cannot see it — dropping it would leave the
    /// watcher reporting itself armed while deaf for good. Two replacements of the same folder
    /// inside a second is enough, which a git rebase in a synced collection can do.
    /// </summary>
    [Fact]
    public void AnErrorTheCooldownHoldsBackIsOwedAndPaidByTheNextStart()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit, rebuildCooldown: TimeSpan.FromMinutes(5));
        watcher.Start();
        var armed = watcher.ArmCount;
        watcher.HandleError();   // rebuilds
        watcher.HandleError();   // inside the cooldown
        Assert.False(watcher.IsArmed, "a watcher owed a rebuild may be on a dead handle");
        watcher.Start();         // AppState re-arms on every reload: this is where the debt is paid
        Assert.True(watcher.IsArmed);
        Assert.Equal(armed + 2, watcher.ArmCount);
    }

    /// <summary>
    /// Only a rebuild that happened spends the cooldown. An attempt that found nothing armed —
    /// a late error after Stop() — rebuilt nothing, and the next real error must still get its
    /// rebuild rather than find the slot taken.
    /// </summary>
    [Fact]
    public void AnAttemptThatRebuiltNothingDoesNotSpendTheCooldown()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit, rebuildCooldown: TimeSpan.FromMinutes(5));
        watcher.Start();
        watcher.Stop();
        watcher.HandleError();   // nothing armed: nothing rebuilt
        watcher.Start();
        var armed = watcher.ArmCount;
        watcher.HandleError();
        Assert.Equal(armed + 1, watcher.ArmCount);
        Assert.True(watcher.IsArmed);
    }

    /// <summary>
    /// A swap builds its new FileSystemWatcher before giving up the live one. The folder can go
    /// between the check that it is there and the build; the path probe stands in for that
    /// window by still reporting the folder after it has been moved away, so the build fails
    /// exactly there. A swap that had retired the live watcher first would leave nothing armed,
    /// and the next Start() would come in cold and re-baseline away whatever change was pending.
    /// Moving the folder back is the proof: it is the folder the kept watcher is on, so the
    /// watcher reports itself armed on it again without another Start(), which a watcher that
    /// had thrown its live one away cannot do. A rename rather than a delete, because a rename
    /// raises no error of its own to race these assertions.
    /// </summary>
    [Fact]
    public void AReArmThatCannotBuildItsWatcherKeepsTheLiveOne()
    {
        var folder = dir.File("collection");
        Directory.CreateDirectory(folder);
        var file = System.IO.Path.Combine(folder, "watched.json");
        File.WriteAllText(file, "one");
        var probe = new FakePathProbe().AddFile(file, File.GetLastWriteTimeUtc(file));
        var counter = new Counter();
        using var watcher = new FileWatcher(file, a => a(), counter.Hit, probe: probe);
        watcher.Start();
        Assert.True(watcher.IsArmed);
        var armed = watcher.ArmCount;
        Thread.Sleep(Settle);

        var away = dir.File("collection-away");
        Directory.Move(folder, away);
        watcher.Start();   // the build fails: it must neither throw nor give up the watcher it has
        Assert.Equal(armed, watcher.ArmCount);

        Directory.Move(away, folder);
        Assert.True(watcher.IsArmed, "the failed swap must not have retired the live watcher");
    }

    [Fact]
    public void AnErrorPastTheCooldownRebuildsAgain()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit, rebuildCooldown: TimeSpan.Zero);
        watcher.Start();
        var armed = watcher.ArmCount;
        watcher.HandleError();
        watcher.HandleError();
        Assert.Equal(armed + 2, watcher.ArmCount);
    }

    /// <summary>
    /// A FileSystemWatcher's error can arrive after the watcher was stopped. There is nothing
    /// armed to recover and nothing was lost, so it must not be reported as a change: Stop() is
    /// public, and a caller that stopped a watcher deliberately is not expecting a callback
    /// from it afterwards.
    /// </summary>
    [Fact]
    public void ALateErrorAfterStopIsNotReportedAsAChange()
    {
        File.WriteAllText(path, "one");
        var counter = new Counter();
        using var watcher = new FileWatcher(path, a => a(), counter.Hit);
        watcher.Start();
        watcher.Stop();
        watcher.HandleError();
        Assert.False(watcher.IsArmed);
        Assert.Equal(0, counter.Count);
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

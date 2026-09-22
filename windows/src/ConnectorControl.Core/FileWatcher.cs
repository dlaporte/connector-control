namespace ConnectorControl.Core;

/// <summary>
/// Watches one file for modification-time changes (the Mac FileWatcher's
/// contract): the parent directory is watched with a name filter, so atomic
/// replaces, in-place writes, deletes and creates are all seen; events are
/// debounced and confirmed against the last-seen mtime; the callback is
/// delivered through <c>marshal</c> (the UI thread in the app) and is dropped
/// if the watcher was stopped or restarted in the meantime.
/// </summary>
public sealed class FileWatcher : IDisposable
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(200);
    /// <summary>How long after a rebuild the next error re-checks instead of rebuilding again.
    /// A folder that keeps erroring must not cost a directory handle and a gap in coverage each
    /// time.</summary>
    private static readonly TimeSpan DefaultRebuildCooldown = TimeSpan.FromSeconds(1);

    private readonly string path;
    private readonly string directory;
    private readonly string fileName;
    private readonly Action<Action> marshal;
    private readonly Action onChange;
    private readonly TimeSpan debounce;
    private readonly TimeSpan rebuildCooldown;
    private readonly IPathProbe probe;
    private readonly object gate = new();

    private FileSystemWatcher? watcher;
    private Timer? timer;
    private DateTime? lastModified;
    private bool disposed;
    private int generation;
    /// <summary>The creation time of the folder <c>watcher</c> was armed on: this watcher's
    /// only handle on which folder that was.</summary>
    private DateTime? armedAt;
    private int armCount;
    /// <summary>Environment.TickCount64 at the last rebuild that actually happened, which is
    /// monotonic and so is not disturbed by the clock moving. Zero means none yet, and the first
    /// error rebuilds. An attempt that rebuilt nothing does not move it.</summary>
    private long lastRebuildAt;
    /// <summary>An error warranted a rebuild that the cooldown held back. The watcher it kept may
    /// be on a dead handle — a folder deleted and recreated under the same name keeps its creation
    /// time on NTFS, so nothing else would ever notice — so the debt stands until it is paid:
    /// IsArmed says no, and the next Start() swaps and re-checks exactly as for a replacement.
    /// That bounds these rebuilds by the reload rate without a timer.</summary>
    private bool rebuildOwed;

    public FileWatcher(string path, Action<Action> marshal, Action onChange, TimeSpan? debounce = null, IPathProbe? probe = null, TimeSpan? rebuildCooldown = null)
    {
        this.path = Path.GetFullPath(path);
        directory = Path.GetDirectoryName(this.path) ?? throw new ArgumentException("Path has no parent directory.", nameof(path));
        fileName = Path.GetFileName(this.path);
        this.marshal = marshal;
        this.onChange = onChange;
        this.debounce = debounce ?? DefaultDebounce;
        this.rebuildCooldown = rebuildCooldown ?? DefaultRebuildCooldown;
        this.probe = probe ?? new RealPathProbe();
    }

    /// <summary>
    /// True while a FileSystemWatcher is active on the parent directory AND that directory is
    /// still the folder at the watched path. A FileSystemWatcher holds a handle on the folder it
    /// was armed on, so a folder replaced wholesale — renamed away with another put in its place,
    /// or deleted and recreated — leaves it watching something that is no longer at the path,
    /// where it goes silent for good; calling that armed would make the caller's re-arm on the
    /// next reload a no-op forever. The same goes for a watcher owed a rebuild the cooldown held
    /// back. A parent that is simply missing stays armed until the error for it is handled, which
    /// is a different case with its own callback.
    /// </summary>
    public bool IsArmed
    {
        get
        {
            DateTime? armed;
            lock (gate)
            {
                if (watcher is null || rebuildOwed)
                {
                    return false;
                }
                armed = armedAt;
            }
            // The probe reads the folder's creation time, which can block for as long as a
            // stalled UNC or cloud path takes to answer. Under the gate that would also block
            // the FileSystemWatcher's own callbacks, which take it: every reload asks each
            // watcher this question, so it must not be able to stall one.
            return !DirectoryWasReplaced(armed);
        }
    }

    /// <summary>Test probe: how many FileSystemWatchers this watcher has armed. A rebuild shows
    /// up here without the test having to depend on what a platform's FileSystemWatcher does
    /// with a handle on a folder that is no longer at the path.</summary>
    internal int ArmCount
    {
        get { lock (gate) { return armCount; } }
    }

    /// <summary>Arms the watcher; a no-op while armed on the folder the path resolves to, and a
    /// swap onto the new folder, plus a re-check against it, when that folder has been replaced or
    /// a rebuild is owed; safe to call again after the parent directory appears. A swap that
    /// cannot build its new watcher keeps the one it has.</summary>
    public void Start()
    {
        FileSystemWatcher? dead = null;
        var replaced = false;
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var cold = watcher is null;
                if (!cold && !rebuildOwed && !DirectoryWasReplaced(armedAt))
                {
                    return;
                }
                if (!probe.DirectoryExists(directory))
                {
                    return;   // caller retries on its next reload
                }
                // A replacement keeps the last-seen modification time, so the first comparison
                // against the new folder's file reports the difference rather than baselining it
                // away; and it keeps the generation, because nothing was stopped and a callback
                // already posted still describes a change the caller has to see.
                if (cold)
                {
                    lastModified = ModificationTime();
                }
                FileSystemWatcher fresh;
                try
                {
                    // Built before the live watcher is given up. The folder can go between the
                    // check above and here, and a swap that failed after retiring the live
                    // watcher would leave nothing armed, so the next Start() would come in cold and
                    // re-baseline away whatever change is pending.
                    fresh = Build();
                }
                catch (Exception ex) when (CannotBuild(ex))
                {
                    return;   // as if the directory were missing: the caller retries on its next reload
                }
                dead = Detach();
                Install(fresh);
                if (cold)
                {
                    generation++;
                }
                else
                {
                    replaced = true;
                }
            }
        }
        finally
        {
            Retire(dead);
        }
        if (replaced)
        {
            // The replacement may already hold a different file, and nothing in it will fire
            // for a change that happened before this watcher existed. Re-check now, as the
            // rebuild in HandleError does, or that change waits for the next unrelated write.
            Schedule();
        }
    }

    public void Stop()
    {
        FileSystemWatcher? fsw;
        Timer? t;
        lock (gate)
        {
            fsw = watcher;
            watcher = null;
            armedAt = null;
            rebuildOwed = false;
            generation++;
            t = timer;
            timer = null;
        }
        Retire(fsw);
        t?.Dispose();
    }

    public void Dispose()
    {
        Stop();
        lock (gate) { disposed = true; }
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Schedule();

    private void OnError(object sender, ErrorEventArgs e) => HandleError(WarrantsRebuild(e.GetException()));

    /// <summary>Whether an error might mean this FileSystemWatcher is watching the wrong folder,
    /// and so is worth the cost of a fresh one. A lost buffer is not: the handle is still on the
    /// right folder, only events were dropped, and a folder syncing a large batch can overflow
    /// the buffer again and again.</summary>
    internal static bool WarrantsRebuild(Exception? error) => error is not InternalBufferOverflowException;

    /// <summary>
    /// A watcher error. When it might mean this FileSystemWatcher is holding the wrong
    /// folder — a watched directory deleted or replaced leaves it on a handle that is no
    /// longer at the path, where it stays permanently silent — a fresh watcher is built on
    /// the directory that is there and the file re-checked against it. That is what covers a
    /// folder deleted and recreated before this ran, the case a Directory.Exists check alone
    /// reads as a lost buffer. A lost buffer itself does not warrant one, because the handle
    /// is still on the right folder; it re-checks the file and keeps the watcher. A second
    /// rebuild inside the cooldown is not done but owed — the watcher it kept may be the dead
    /// one — so it re-checks too, and the next Start() pays the debt; a folder that keeps
    /// erroring therefore costs re-checks and one rebuild per reload rather than a teardown
    /// apiece. Only a directory that is really gone disarms
    /// and reports, leaving the caller's next Start() (each reload re-arms it) to build a
    /// fresh one. The
    /// deletion is delivered directly rather than through Schedule(): Stop() disposes
    /// whatever debounce timer is pending, so a Schedule()-then-Stop() sequence would
    /// dispose the very timer meant to report this change and the deletion would
    /// never reach the caller. No generation guard here — Stop() has
    /// already run, so this is a final, deliberate notification, not a stale check;
    /// only a Dispose() that raced the error handler suppresses it.
    /// Internal so the deleted-directory path is testable without provoking the OS.
    /// </summary>
    internal void HandleError(bool rebuild = true)
    {
        if (probe.DirectoryExists(directory))
        {
            if (!rebuild)
            {
                // Re-check the file and keep the watcher: Schedule() does nothing of its own
                // when nothing is armed, so a late error after a Stop() reports nothing.
                Schedule();
                return;
            }
            switch (Rebuild())
            {
                case Rebuilt.Yes:
                case Rebuilt.Owed:
                    Schedule();
                    return;
                case Rebuilt.NothingArmed:
                    return;   // a late error from a watcher already stopped: nothing to recover
                default:
                    break;    // Rebuilt.Failed: a directory that cannot be watched is as good as gone
            }
        }
        Stop();
        marshal(() =>
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
            }
            onChange();
        });
    }

    /// <summary>What an attempted rebuild did, which is what decides how the error that
    /// prompted it is treated.</summary>
    private enum Rebuilt
    {
        /// <summary>A fresh FileSystemWatcher is live on the path.</summary>
        Yes,
        /// <summary>The cooldown held the rebuild back. It is recorded as owed, for the next
        /// Start() to pay, rather than dropped.</summary>
        Owed,
        /// <summary>Nothing was armed, so this is a late error from a watcher already stopped:
        /// there is nothing to recover and nothing to report.</summary>
        NothingArmed,
        /// <summary>There is a directory at the path but it could not be watched, which the
        /// caller treats the same way as a directory that is gone.</summary>
        Failed,
    }

    /// <summary>
    /// Builds and enables a FileSystemWatcher on the parent directory without making it the live
    /// one, so a caller whose build fails still has the watcher it had. Throws what building one
    /// throws when the directory has gone; <see cref="CannotBuild"/> recognises it.
    /// </summary>
    private FileSystemWatcher Build()
    {
        var fsw = new FileSystemWatcher(directory)
        {
            Filter = fileName,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            InternalBufferSize = 64 * 1024,
        };
        try
        {
            fsw.Changed += OnEvent;
            fsw.Created += OnEvent;
            fsw.Deleted += OnEvent;
            fsw.Renamed += OnEvent;
            fsw.Error += OnError;
            fsw.EnableRaisingEvents = true;
        }
        catch
        {
            fsw.Dispose();   // enabling can throw, and a half-built watcher still holds a handle
            throw;
        }
        return fsw;
    }

    /// <summary>Makes a freshly built watcher the live one and records which folder it is on;
    /// whatever was owed is paid by it. The caller holds the gate and has already detached the
    /// previous watcher.</summary>
    private void Install(FileSystemWatcher fsw)
    {
        armedAt = CreationTime(directory);
        armCount++;
        rebuildOwed = false;
        watcher = fsw;
    }

    /// <summary>What building a FileSystemWatcher throws when its directory has just gone.</summary>
    private static bool CannotBuild(Exception ex) => FileSystemErrors.IsTransient(ex) || ex is ArgumentException;

    /// <summary>Takes the live FileSystemWatcher out for the caller to Retire outside the gate:
    /// disposing one can block on its own callbacks, and those callbacks take the gate. Caller
    /// holds the gate.</summary>
    private FileSystemWatcher? Detach()
    {
        var previous = watcher;
        watcher = null;
        return previous;
    }

    /// <summary>Unsubscribes and disposes a watcher this object is finished with. The handlers
    /// have to come off: a retired watcher that keeps them can still deliver one last event, and
    /// an error among them would drive a rebuild on behalf of a folder nobody is watching any
    /// more.</summary>
    private void Retire(FileSystemWatcher? fsw)
    {
        if (fsw is null)
        {
            return;
        }
        fsw.EnableRaisingEvents = false;
        fsw.Changed -= OnEvent;
        fsw.Created -= OnEvent;
        fsw.Deleted -= OnEvent;
        fsw.Renamed -= OnEvent;
        fsw.Error -= OnError;
        fsw.Dispose();
    }

    /// <summary>
    /// Swaps the live FileSystemWatcher for a fresh one on the same path, at most once per
    /// cooldown; one held back by the cooldown is owed rather than dropped. Never throws: it runs
    /// on the FileSystemWatcher's own callback thread.
    /// </summary>
    private Rebuilt Rebuild()
    {
        FileSystemWatcher? dead = null;
        try
        {
            lock (gate)
            {
                if (disposed || watcher is null)
                {
                    return Rebuilt.NothingArmed;
                }
                var now = Environment.TickCount64;
                if (lastRebuildAt != 0 && now - lastRebuildAt < (long)rebuildCooldown.TotalMilliseconds)
                {
                    rebuildOwed = true;
                    return Rebuilt.Owed;
                }
                var fresh = Build();   // before the live watcher is given up, as in Start()
                dead = Detach();
                Install(fresh);
                // Stamped only now that a rebuild has happened: an attempt that found nothing
                // armed, or could not build, must not spend the slot the next real error needs.
                lastRebuildAt = now;
                return Rebuilt.Yes;
            }
        }
        catch (Exception ex) when (CannotBuild(ex))
        {
            return Rebuilt.Failed;   // the directory went away between the check and the build
        }
        finally
        {
            Retire(dead);
        }
    }

    /// <summary>
    /// Whether the folder at the watched path is a different one from the folder armed on, told
    /// by its creation time — the only folder identity managed code has on every platform, with
    /// no handle and no inode to compare. It is a weaker test than the Mac's device and inode,
    /// in both directions, and both are accepted here rather than paying for a Windows-only
    /// handle comparison:
    ///
    /// <para>It can say yes when nothing was replaced. Creation times are settable, sync clients
    /// set them on items they materialize, and a FAT or exFAT volume's reported UTC creation
    /// time shifts across a DST boundary. Each of those costs one rebuild, after which the new
    /// time is what is recorded, so it is self-healing.</para>
    ///
    /// <para>It can say no when a folder was replaced. NTFS restores the creation time of a name
    /// deleted and recreated in the same parent within about fifteen seconds, and that case is
    /// caught instead by the rebuild in HandleError, since deleting the folder raises an error.
    /// What neither half catches is a folder renamed away and replaced by one whose creation
    /// time was preserved — restored from a backup, copied with robocopy /DCOPY:T, or moved back
    /// from elsewhere on the same volume — because a rename raises no error for the rebuild to
    /// act on. Nor does a file system that does not store a creation time, such as SMB to a
    /// Samba host, where both reads return the same constant and this check quietly does
    /// nothing.</para>
    ///
    /// <para>A path that resolves to nothing is not a replacement: that is the lost-directory
    /// case, which HandleError owns. Takes no lock: it is given the snapshot to compare against,
    /// because reading a folder's creation time can block.</para>
    /// </summary>
    private bool DirectoryWasReplaced(DateTime? armed)
    {
        if (!probe.DirectoryExists(directory))
        {
            return false;
        }
        var now = CreationTime(directory);
        return now is not null && armed is not null && now != armed;
    }

    /// <summary>
    /// The folder's creation time. For a path that does not exist, .NET does not throw: it
    /// returns 1601-01-01 UTC, which differs from any time recorded at arm time, so a missing
    /// folder is kept from reading as a replacement only by DirectoryWasReplaced's own existence
    /// check. Null comes back only when the read itself fails for a transient reason, and a null
    /// is never read as a replacement.
    /// </summary>
    private static DateTime? CreationTime(string directory)
    {
        try
        {
            return Directory.GetCreationTimeUtc(directory);
        }
        catch (Exception ex) when (FileSystemErrors.IsTransient(ex))
        {
            return null;
        }
    }

    /// <summary>Restart the debounce timer; the check runs once the burst ends.</summary>
    private void Schedule()
    {
        lock (gate)
        {
            if (watcher is null)
            {
                return;
            }
            timer ??= new Timer(_ => CheckForChange(), null, Timeout.Infinite, Timeout.Infinite);
            timer.Change(debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void CheckForChange()
    {
        bool changed;
        int observed;
        lock (gate)
        {
            if (watcher is null)
            {
                return;
            }
            var current = ModificationTime();
            changed = current != lastModified;
            lastModified = current;
            observed = generation;
        }
        if (!changed)
        {
            return;
        }
        // The callback re-checks on the thread `marshal` delivers to (the UI thread in
        // the app, where Stop/Start also run), so a Stop() that raced this check wins.
        marshal(() =>
        {
            lock (gate)
            {
                if (watcher is null || generation != observed)
                {
                    return;
                }
            }
            onChange();
        });
    }

    private DateTime? ModificationTime()
    {
        try
        {
            return probe.FileExists(path) ? probe.LastWriteTimeUtc(path) : null;
        }
        catch (Exception ex) when (FileSystemErrors.IsTransient(ex))
        {
            return null;
        }
    }
}

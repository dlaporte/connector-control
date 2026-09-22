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

    private readonly string path;
    private readonly string directory;
    private readonly string fileName;
    private readonly Action<Action> marshal;
    private readonly Action onChange;
    private readonly TimeSpan debounce;
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

    public FileWatcher(string path, Action<Action> marshal, Action onChange, TimeSpan? debounce = null, IPathProbe? probe = null)
    {
        this.path = Path.GetFullPath(path);
        directory = Path.GetDirectoryName(this.path) ?? throw new ArgumentException("Path has no parent directory.", nameof(path));
        fileName = Path.GetFileName(this.path);
        this.marshal = marshal;
        this.onChange = onChange;
        this.debounce = debounce ?? DefaultDebounce;
        this.probe = probe ?? new RealPathProbe();
    }

    /// <summary>
    /// True while a FileSystemWatcher is active on the parent directory AND that directory is
    /// still the folder at the watched path. A FileSystemWatcher holds a handle on the folder it
    /// was armed on, so a folder replaced wholesale — renamed away with another put in its place,
    /// or deleted and recreated — leaves it watching something that is no longer at the path,
    /// where it goes silent for good; calling that armed would make the caller's re-arm on the
    /// next reload a no-op forever. A parent that is simply missing stays armed until the error
    /// for it is handled, which is a different case with its own callback.
    /// </summary>
    public bool IsArmed
    {
        get { lock (gate) { return watcher is not null && !DirectoryWasReplaced(); } }
    }

    /// <summary>Test probe: how many FileSystemWatchers this watcher has armed. A rebuild shows
    /// up here without the test having to depend on what a platform's FileSystemWatcher does
    /// with a handle on a folder that is no longer at the path.</summary>
    internal int ArmCount
    {
        get { lock (gate) { return armCount; } }
    }

    /// <summary>Arms the watcher; a no-op while armed on the folder the path resolves to, and a
    /// swap onto the new folder when that folder has been replaced; safe to call again after the
    /// parent directory appears.</summary>
    public void Start()
    {
        FileSystemWatcher? dead = null;
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var cold = watcher is null;
                if (!cold && !DirectoryWasReplaced())
                {
                    return;
                }
                if (!Directory.Exists(directory))
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
                dead = Detach();
                Arm();
                if (cold)
                {
                    generation++;
                }
            }
        }
        finally
        {
            Retire(dead);
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
            generation++;
            t = timer;
            timer = null;
        }
        if (fsw is not null)
        {
            fsw.EnableRaisingEvents = false;
            fsw.Dispose();
        }
        t?.Dispose();
    }

    public void Dispose()
    {
        Stop();
        lock (gate) { disposed = true; }
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Schedule();

    private void OnError(object sender, ErrorEventArgs e) => HandleError();

    /// <summary>
    /// A watcher error. Whatever raised it, this FileSystemWatcher is no longer to be
    /// trusted: a buffer overflow means events were lost, and a watched directory that was
    /// deleted or replaced leaves it holding a handle on a folder that is no longer at the
    /// path, where it stays permanently silent. So while there is still a directory at the
    /// path, build a fresh watcher on it and re-check the file — which covers a plain buffer
    /// overflow as well as a folder that was deleted and recreated before this ran, the case
    /// a Directory.Exists check alone reads as an overflow. Only a directory that is really
    /// gone disarms and reports, leaving the caller's next Start() (each reload re-arms it)
    /// to build a fresh one. The
    /// deletion is delivered directly rather than through Schedule(): Stop() disposes
    /// whatever debounce timer is pending, so a Schedule()-then-Stop() sequence would
    /// dispose the very timer meant to report this change and the deletion would
    /// never reach the caller. No generation guard here — Stop() has
    /// already run, so this is a final, deliberate notification, not a stale check;
    /// only a Dispose() that raced the error handler suppresses it.
    /// Internal so the deleted-directory path is testable without provoking the OS.
    /// </summary>
    internal void HandleError()
    {
        if (Directory.Exists(directory) && Rebuild())
        {
            Schedule();
            return;
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

    /// <summary>
    /// Builds a FileSystemWatcher on the parent directory and makes it the live one, recording
    /// which folder that was. The caller holds the gate and has already detached any previous
    /// watcher.
    /// </summary>
    private void Arm()
    {
        var fsw = new FileSystemWatcher(directory)
        {
            Filter = fileName,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            InternalBufferSize = 64 * 1024,
        };
        fsw.Changed += OnEvent;
        fsw.Created += OnEvent;
        fsw.Deleted += OnEvent;
        fsw.Renamed += OnEvent;
        fsw.Error += OnError;
        fsw.EnableRaisingEvents = true;
        armedAt = CreationTime(directory);
        armCount++;
        watcher = fsw;
    }

    /// <summary>Takes the live FileSystemWatcher out for the caller to Retire outside the gate:
    /// disposing one can block on its own callbacks, and those callbacks take the gate. Caller
    /// holds the gate.</summary>
    private FileSystemWatcher? Detach()
    {
        var previous = watcher;
        watcher = null;
        return previous;
    }

    private static void Retire(FileSystemWatcher? fsw)
    {
        if (fsw is null)
        {
            return;
        }
        fsw.EnableRaisingEvents = false;
        fsw.Dispose();
    }

    /// <summary>
    /// Swaps the live FileSystemWatcher for a fresh one on the same path. False when nothing was
    /// armed or the directory could not be watched after all, which the caller then treats as a
    /// lost directory. Never throws: it runs on the FileSystemWatcher's own callback thread.
    /// </summary>
    private bool Rebuild()
    {
        FileSystemWatcher? dead = null;
        try
        {
            lock (gate)
            {
                if (disposed || watcher is null)
                {
                    return false;
                }
                dead = Detach();
                Arm();
                return true;
            }
        }
        catch (Exception ex) when (FileSystemErrors.IsTransient(ex) || ex is ArgumentException)
        {
            return false;   // the directory went away between the check and the arm
        }
        finally
        {
            Retire(dead);
        }
    }

    /// <summary>
    /// Whether the folder at the watched path is a different one from the folder armed on.
    /// Creation time is the only folder identity managed code has on every platform — there is
    /// no handle and no inode to compare — so this is deliberately one-directional: a folder
    /// reporting the same creation time may still be a replacement, because NTFS restores the
    /// creation time of a name deleted and recreated in the same parent within seconds, and that
    /// case is caught instead by the rebuild in HandleError. What it cannot do is claim a
    /// replacement that did not happen, since a folder keeps its creation time. A path that
    /// resolves to nothing is not a replacement: that is the lost-directory case, which
    /// HandleError owns. Caller holds the gate.
    /// </summary>
    private bool DirectoryWasReplaced()
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }
        var now = CreationTime(directory);
        return now is not null && armedAt is not null && now != armedAt;
    }

    private static DateTime? CreationTime(string directory)
    {
        try
        {
            return Directory.GetCreationTimeUtc(directory);
        }
        catch (Exception ex) when (FileSystemErrors.IsTransient(ex))
        {
            return null;   // unknown, which is never read as a replacement
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

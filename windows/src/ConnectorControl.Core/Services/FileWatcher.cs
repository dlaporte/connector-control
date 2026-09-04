namespace ConnectorControl.Core.Services;

/// <summary>
/// Watches one file for modification-time changes (the Mac FileWatcher's
/// contract): the parent directory is watched with a name filter, so atomic
/// replaces, in-place writes, deletes and creates are all seen; events are
/// debounced and confirmed against the last-seen mtime; the callback is
/// delivered through <c>marshal</c> (the UI thread in the app).
/// </summary>
public sealed class FileWatcher : IDisposable
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(200);

    private readonly string path;
    private readonly string directory;
    private readonly string fileName;
    private readonly Action onChange;
    private readonly Action<Action> marshal;
    private readonly TimeSpan debounce;
    private readonly object gate = new();

    private FileSystemWatcher? watcher;
    private Timer? timer;
    private DateTime? lastModified;
    private bool disposed;

    public FileWatcher(string path, Action onChange, Action<Action> marshal, TimeSpan? debounce = null)
    {
        this.path = Path.GetFullPath(path);
        directory = Path.GetDirectoryName(this.path) ?? throw new ArgumentException("Path has no parent directory.", nameof(path));
        fileName = Path.GetFileName(this.path);
        this.onChange = onChange;
        this.marshal = marshal;
        this.debounce = debounce ?? DefaultDebounce;
    }

    /// <summary>True while a FileSystemWatcher is active on the parent directory.</summary>
    public bool IsArmed
    {
        get { lock (gate) { return watcher is not null; } }
    }

    /// <summary>Arms the watcher; a no-op while armed; safe to call again after the parent directory appears.</summary>
    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (watcher is not null)
            {
                return;
            }
            if (!Directory.Exists(directory))
            {
                return;   // caller retries on its next reload
            }
            lastModified = ModificationTime();
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
            watcher = fsw;
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

    private void OnError(object sender, ErrorEventArgs e) => Schedule();   // buffer overflow: re-check rather than miss a change

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
        lock (gate)
        {
            if (watcher is null)
            {
                return;
            }
            var current = ModificationTime();
            changed = current != lastModified;
            lastModified = current;
        }
        if (changed)
        {
            marshal(onChange);
        }
    }

    private DateTime? ModificationTime()
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

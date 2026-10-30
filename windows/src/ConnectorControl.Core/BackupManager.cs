using System.Globalization;

namespace ConnectorControl.Core;

/// <summary>
/// Backups hold the same secrets as the file they copy, and the backups tree is always the app's own
/// (never the synced folder), so every copy goes through <see cref="AtomicFile.Write"/>: the file is
/// owner-only from its create call, and any directory that has to be created - the default store dir
/// included, when this is the first write of a fresh install - is private too, like the Mac's 0700.
/// </summary>
public sealed class BackupManager
{
    /// <summary>Same-millisecond backups tried before giving up and overwriting the last one.</summary>
    private const int CollisionBound = 100;
    /// <summary>The keep count ConfigService and SettingsStore fall back to.</summary>
    public const int DefaultKeepCount = 20;

    public string BackupsDir { get; }
    public int KeepCount { get; }

    public BackupManager(string backupsDir, int keepCount = DefaultKeepCount)
    {
        BackupsDir = backupsDir;
        KeepCount = keepCount;
    }

    /// <summary>First-run snapshot; written once, never pruned.</summary>
    public void EnsureOriginalSnapshot(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        var baseName = Path.GetFileNameWithoutExtension(path);
        var dest = OriginalPath(baseName);
        if (File.Exists(dest))
        {
            return;
        }
        AtomicFile.Write(File.ReadAllBytes(path), dest);
    }

    /// <summary>The <c>.original.json</c> snapshot's path for a series - what <see cref="EnsureOriginalSnapshot"/>
    /// writes once and never prunes - when it exists on disk; null otherwise.</summary>
    public string? OriginalSnapshotPath(string series)
    {
        var dest = OriginalPath(series);
        return File.Exists(dest) ? dest : null;
    }

    /// <summary>Every backup the Restore sheet offers for a series: the timestamped ones newest first, then
    /// the first-run original when there is one.</summary>
    public IReadOnlyList<string> RestorableBackups(string series)
    {
        var found = new List<string>(Backups(series));
        if (OriginalSnapshotPath(series) is { } original)
        {
            found.Add(original);
        }
        return found;
    }

    private string OriginalPath(string series) => Path.Combine(BackupsDir, $"{series}.original.json");

    /// <summary>
    /// Returns the existing newest backup instead of writing a duplicate when
    /// the file's content is unchanged: regeneration backs up without user
    /// action, and identical snapshots would only churn real history out of
    /// the retention window. Dedup is against the newest snapshot only, so an
    /// A → B → A sequence still records the return to A. Null when the source
    /// is missing.
    /// </summary>
    public string? BackUp(string path, string series, DateTime? now = null)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        var existing = Backups(series);
        // Read once: the bytes compared against the newest backup are the bytes written, so a
        // file that changes mid-backup cannot land a copy the dedup check never saw.
        var current = File.ReadAllBytes(path);
        if (existing.Count > 0 && SameContent(existing[0], current))
        {
            return existing[0];
        }
        var stamp = BackupTimestamp.From(now ?? DateTime.UtcNow);
        var dest = Path.Combine(BackupsDir, $"{series}.{stamp}.json");
        int counter = 2;
        while (File.Exists(dest) && counter <= CollisionBound)
        {
            dest = Path.Combine(BackupsDir, $"{series}.{stamp}-{counter}.json");
            counter++;
        }
        // Bound exhausted (CollisionBound same-millisecond backups already exist): the write
        // replaces the last candidate rather than throwing.
        AtomicFile.Write(current, dest);
        Prune(existing.Contains(dest, StringComparer.Ordinal) ? existing : [.. existing, dest]);
        return dest;
    }

    /// <summary>Timestamped backups for a series (full paths), newest first. Excludes <c>.original</c>.</summary>
    public IReadOnlyList<string> Backups(string series)
    {
        if (!Directory.Exists(BackupsDir))
        {
            return [];
        }
        var prefix = series + ".";
        return Directory.EnumerateFiles(BackupsDir)
            .Where(p =>
            {
                var name = Path.GetFileName(p);
                return name.StartsWith(prefix, StringComparison.Ordinal)
                    && !name.Contains(".original.", StringComparison.Ordinal);
            })
            .Order(Comparer<string>.Create(NewestFirst))
            .ToList();
    }

    /// <summary>
    /// Newest first: by stamp, then by the counter a same-millisecond backup takes, a name without
    /// one being the first of its millisecond. Sorting the names as text would not do: <c>-</c> sorts
    /// before <c>.</c>, so <c>&lt;stamp&gt;-2.json</c> would list below <c>&lt;stamp&gt;.json</c>, and
    /// <c>-10</c> below <c>-2</c>. A comparison, where Swift's <c>sorted</c> takes a predicate.
    /// </summary>
    internal static int NewestFirst(string a, string b)
    {
        var x = Order(Path.GetFileName(a));
        var y = Order(Path.GetFileName(b));
        return x.Stamp != y.Stamp ? string.CompareOrdinal(y.Stamp, x.Stamp) : y.Counter.CompareTo(x.Counter);
    }

    /// <summary>
    /// When a backup was taken, read from its name: <c>&lt;series&gt;.&lt;stamp&gt;.json</c>, or
    /// <c>-&lt;n&gt;</c> after the stamp. Null for a name that carries no stamp, such as the first-run
    /// original.
    /// </summary>
    public static DateTime? TakenAt(string backup)
    {
        var stamp = Order(Path.GetFileName(backup)).Stamp;
        return BackupTimestamp.Parse(stamp[(stamp.LastIndexOf('.') + 1)..]);
    }

    /// <summary>
    /// A backup name's stamp and counter: <c>&lt;series&gt;.&lt;stamp&gt;.json</c> is counter 1, and
    /// <c>&lt;series&gt;.&lt;stamp&gt;-&lt;n&gt;.json</c> counter n. The stamp ends in <c>Z</c>, so the
    /// counter is what follows it.
    /// </summary>
    private static (string Stamp, int Counter) Order(string name)
    {
        var @base = name.EndsWith(".json", StringComparison.Ordinal) ? name[..^".json".Length] : name;
        var z = @base.LastIndexOf('Z');
        if (z < 0)
        {
            return (@base, 1);
        }
        var rest = @base[(z + 1)..];
        if (!rest.StartsWith('-') || !int.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var counter))
        {
            return (@base, 1);
        }
        return (@base[..(z + 1)], counter);
    }

    /// <summary>
    /// <paramref name="listing"/> is the caller's own pre-write directory read plus the file it just
    /// wrote, sorted here the same way <see cref="Backups"/> sorts, so pruning does not re-list a
    /// directory <see cref="BackUp"/> already just listed.
    /// </summary>
    private void Prune(IEnumerable<string> listing)
    {
        foreach (var stale in listing.Order(Comparer<string>.Create(NewestFirst)).Skip(KeepCount))
        {
            // Best effort: a backup that briefly refuses deletion (locked by a scanner, say) is
            // pruned next time; a rotation must never fail the write it is cleaning up after.
            FileSystemErrors.TryDelete(stale);
        }
    }

    /// <summary>Whether the backup at <paramref name="backup"/> holds exactly <paramref name="current"/>; one
    /// that cannot be read counts as different, so the backup is taken.</summary>
    private static bool SameContent(string backup, byte[] current)
    {
        try
        {
            return File.ReadAllBytes(backup).AsSpan().SequenceEqual(current);
        }
        catch (Exception ex) when (FileSystemErrors.IsTransient(ex))
        {
            return false;
        }
    }
}

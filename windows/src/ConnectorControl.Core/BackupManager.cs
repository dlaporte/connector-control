namespace ConnectorControl.Core;

/// <summary>
/// Backups hold the same secrets as the file they copy, and the backups tree is always the app's own
/// (never the synced folder), so every copy goes through <see cref="AtomicFile.Write"/>: the file is
/// owner-only from its create call, and any directory that has to be created — the default store dir
/// included, when this is the first write of a fresh install — is private too, like the Mac's 0700.
/// </summary>
public sealed class BackupManager
{
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
        var dest = Path.Combine(BackupsDir, $"{baseName}.original.json");
        if (File.Exists(dest))
        {
            return;
        }
        AtomicFile.Write(File.ReadAllBytes(path), dest);
    }

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
        var newest = Backups(series).FirstOrDefault();
        if (newest is not null && SameContent(newest, path))
        {
            return newest;
        }
        var stamp = BackupTimestamp.From(now ?? DateTime.UtcNow);
        var dest = Path.Combine(BackupsDir, $"{series}.{stamp}.json");
        int counter = 2;
        while (File.Exists(dest) && counter <= 100)
        {
            dest = Path.Combine(BackupsDir, $"{series}.{stamp}-{counter}.json");
            counter++;
        }
        // Bound exhausted: the write replaces the last candidate rather than throwing.
        AtomicFile.Write(File.ReadAllBytes(path), dest);
        Prune(series);
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
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
    }

    private void Prune(string series)
    {
        foreach (var stale in Backups(series).Skip(KeepCount))
        {
            // Best effort: a backup that briefly refuses deletion (locked by a scanner, say) is
            // pruned next time; a rotation must never fail the write it is cleaning up after.
            FileSystemErrors.TryDelete(stale);
        }
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch (Exception ex) when (FileSystemErrors.IsTransient(ex))
        {
            return false;
        }
    }
}

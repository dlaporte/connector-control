namespace ConnectorControl.Core;

public sealed class BackupManager
{
    public string BackupsDir { get; }
    public int KeepCount { get; }

    public BackupManager(string backupsDir, int keepCount = 20)
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
        EnsureBackupsDir();
        File.Copy(path, dest);
        OwnerOnlyAcl.TryApply(dest);   // backups can hold env-var secrets
    }

    /// <summary>
    /// Returns the existing newest backup instead of writing a duplicate when the
    /// file's content is unchanged (dedup against the newest snapshot only, so an
    /// A → B → A sequence still records the return to A). Null when the source is missing.
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
        EnsureBackupsDir();
        var stamp = BackupTimestamp.From(now ?? DateTime.UtcNow);
        var dest = Path.Combine(BackupsDir, $"{series}.{stamp}.json");
        int counter = 2;
        while (File.Exists(dest) && counter <= 100)
        {
            dest = Path.Combine(BackupsDir, $"{series}.{stamp}-{counter}.json");
            counter++;
        }
        if (File.Exists(dest))
        {
            File.Delete(dest);   // bound exhausted: overwrite rather than throw
        }
        File.Copy(path, dest);
        OwnerOnlyAcl.TryApply(dest);
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

    private void EnsureBackupsDir()
    {
        if (!Directory.Exists(BackupsDir))
        {
            Directory.CreateDirectory(BackupsDir);
            OwnerOnlyAcl.TryApply(BackupsDir);
        }
    }

    private void Prune(string series)
    {
        foreach (var stale in Backups(series).Skip(KeepCount))
        {
            File.Delete(stale);
        }
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

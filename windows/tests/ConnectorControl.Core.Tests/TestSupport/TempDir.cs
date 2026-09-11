namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>A unique temp directory deleted on dispose (mirrors the Swift tests' setUp/tearDown).</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir(string prefix = "cc")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    /// <summary>Absolute path of a file or directory inside this temp dir (not created).</summary>
    public string File(string relative) => System.IO.Path.Combine(Path, relative);

    /// <summary>
    /// Writes <paramref name="content"/> and pushes the file's modification time 2 seconds
    /// past whatever the write itself just gave it — a deterministic stand-in for the
    /// wall-clock sleep a file-watcher test would otherwise need so a second write's mtime is
    /// unambiguously later than the first's, even on file systems with coarse mtime
    /// resolution. <see cref="BumpModificationTime"/> is the half of this a caller needs on
    /// its own when the write goes through a different path (e.g. an atomic replace).
    /// </summary>
    public static void Touch(string path, string content = "")
    {
        System.IO.File.WriteAllText(path, content);
        BumpModificationTime(path);
    }

    public static void BumpModificationTime(string path) =>
        System.IO.File.SetLastWriteTimeUtc(path, System.IO.File.GetLastWriteTimeUtc(path) + TimeSpan.FromSeconds(2));

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

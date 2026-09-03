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

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

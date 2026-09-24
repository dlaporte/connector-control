namespace ConnectorControl.Core;

/// <summary>The file-system questions path resolution and FileWatcher ask, so they are testable
/// on any OS.</summary>
public interface IPathProbe
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    IEnumerable<string> EnumerateDirectories(string path);
    DateTime? LastWriteTimeUtc(string path);
    /// <summary>A directory's creation time: FileWatcher's only handle on which folder it armed
    /// on. For a path that does not exist .NET returns 1601-01-01 UTC rather than throwing.</summary>
    DateTime CreationTimeUtc(string path);
}

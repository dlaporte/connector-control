namespace ConnectorControl.Core;

/// <summary>The three file-system questions path resolution asks, so it is testable on any OS.</summary>
public interface IPathProbe
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    IEnumerable<string> EnumerateDirectories(string path);
}

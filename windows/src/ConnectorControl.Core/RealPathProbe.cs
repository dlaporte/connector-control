namespace ConnectorControl.Core;

public sealed class RealPathProbe : IPathProbe
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public DateTime? LastWriteTimeUtc(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;

    public IEnumerable<string> EnumerateDirectories(string path)
    {
        if (!Directory.Exists(path))
        {
            return [];
        }
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}

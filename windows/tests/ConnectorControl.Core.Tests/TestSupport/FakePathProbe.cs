namespace ConnectorControl.Core.Tests.TestSupport;

public sealed class FakePathProbe : IPathProbe
{
    private readonly HashSet<string> files = new(StringComparer.Ordinal);
    private readonly HashSet<string> dirs = new(StringComparer.Ordinal);

    public FakePathProbe AddFile(string path)
    {
        files.Add(path);
        for (var d = Path.GetDirectoryName(path); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
        {
            dirs.Add(d);
        }
        return this;
    }

    public FakePathProbe AddDirectory(string path)
    {
        for (var d = path; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
        {
            dirs.Add(d);
        }
        return this;
    }

    public bool FileExists(string path) => files.Contains(path);

    public bool DirectoryExists(string path) => dirs.Contains(path);

    public IEnumerable<string> EnumerateDirectories(string path) =>
        dirs.Where(d => string.Equals(Path.GetDirectoryName(d), path, StringComparison.Ordinal));
}

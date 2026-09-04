namespace ConnectorControl.Core.Tests.TestSupport;

public sealed class FakePathProbe : IPathProbe
{
    private readonly HashSet<string> files = new(StringComparer.Ordinal);
    private readonly HashSet<string> dirs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime?> lastWriteTimes = new(StringComparer.Ordinal);

    public FakePathProbe AddFile(string path, DateTime? lastWriteUtc = null)
    {
        files.Add(path);
        lastWriteTimes[path] = lastWriteUtc ?? DateTime.UnixEpoch;
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

    public DateTime? LastWriteTimeUtc(string path) => lastWriteTimes.TryGetValue(path, out var t) ? t : null;
}

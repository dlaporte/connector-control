using System.Collections;

namespace ConnectorControl.Core.State;

/// <summary>Everything AppPathsResolver needs from the machine, injectable for tests.</summary>
public sealed record PathContext(IReadOnlyDictionary<string, string> Environment, KnownFolders Folders, IPathProbe Probe)
{
    public static PathContext Live()
    {
        // Windows environment names are case-insensitive; keep Mac (dev) lookups exact.
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var environment = new Dictionary<string, string>(comparer);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                environment[key] = value;
            }
        }
        return new PathContext(environment, KnownFolders.Current(), new RealPathProbe());
    }
}

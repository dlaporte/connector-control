using System.Text.Json;

namespace ConnectorControl.Core;

public static class MasterStoreIO
{
    /// <summary>
    /// Missing file → empty store. Corrupt file → moved aside to
    /// <c>mcps.corrupt.&lt;timestamp&gt;.json</c> (returned) and an empty store;
    /// if it cannot be moved, the original path is returned instead.
    /// </summary>
    public static (MasterStore Store, string? CorruptFilePath) Load(string path, DateTime? now = null)
    {
        if (!File.Exists(path))
        {
            return (MasterStore.Empty(), null);
        }
        try
        {
            var store = MasterStore.FromJson(JsonValue.Parse(File.ReadAllBytes(path)));
            // Self-heal a decoded-but-inconsistent activeProfile: never crash.
            if (!store.Profiles.ContainsKey(store.ActiveProfile))
            {
                var fallback = store.Profiles.Keys.Order(StringComparer.Ordinal).FirstOrDefault();
                if (fallback is not null)
                {
                    store.ActiveProfile = fallback;
                }
                else
                {
                    store.Profiles["Default"] = new Profile();
                    store.ActiveProfile = "Default";
                }
            }
            return (store, null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException or UnauthorizedAccessException)
        {
            var stamp = BackupTimestamp.From(now ?? DateTime.UtcNow);
            var aside = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $"mcps.corrupt.{stamp}.json");
            try
            {
                File.Move(path, aside, overwrite: false);
                return (MasterStore.Empty(), aside);
            }
            catch (IOException)
            {
                return (MasterStore.Empty(), path);   // couldn't move it aside; it stays in place
            }
        }
    }

    public static void Save(MasterStore store, string path) => AtomicFile.Write(store.ToJson().Serialize(), path);

    /// <summary>Side-effect-free peek: null when missing or undecodable. Never moves a corrupt file.</summary>
    public static MasterStore? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            return MasterStore.FromJson(JsonValue.Parse(File.ReadAllBytes(path)));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

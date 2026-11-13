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
            // A decoded-but-inconsistent activeCollection (hand-edited or corrupted
            // file) is self-healed by the MasterStore constructor itself - see
            // its comment - so FromJson always returns a store whose
            // ActiveCollection names an existing collection.
            return (Decode(File.ReadAllBytes(path)), null);
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
            catch (Exception moveEx) when (FileSystemErrors.IsTransient(moveEx))
            {
                return (MasterStore.Empty(), path);   // couldn't move it aside; it stays in place
            }
        }
    }

    public static AtomicWriteResult Save(MasterStore store, string path) => AtomicFile.Write(store.ToJson().Serialize(), path);

    /// <summary>
    /// Side-effect-free peek: null when missing or undecodable. Unlike
    /// <see cref="Load"/>, never moves a corrupt file aside - used by the
    /// store watcher to classify an on-disk change (own write echo, external
    /// edit, or a sync tool's mid-write partial) before deciding to adopt it.
    /// </summary>
    public static MasterStore? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            return Decode(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A master list's bytes as <see cref="Load"/> and <see cref="Read"/> decode them: a backup read
    /// once is decoded here, so what is shown from it is what is restored from it. Throws
    /// <see cref="JsonException"/> or <see cref="FormatException"/>.
    /// </summary>
    public static MasterStore Decode(byte[] data) => MasterStore.FromJson(JsonValue.Parse(data));
}

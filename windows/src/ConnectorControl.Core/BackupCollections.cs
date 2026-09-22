namespace ConnectorControl.Core;

/// <summary>
/// Which collection each Claude-config backup was taken from. Claude's file holds whichever
/// collection it was last applied from, so a restore belongs in that collection and no other. The
/// record is one small index beside the backups, so every backup stays a byte copy of Claude's
/// file. A backup the index does not name — one from before it was kept, the first-run original, a
/// file chosen from elsewhere — has no recorded collection.
///
/// The name is chosen so no backup series lists it: every series is matched by "&lt;series&gt;.".
///
/// Mirror: Sources/ConnectorControlCore/BackupCollections.swift
/// </summary>
public static class BackupCollections
{
    public const string FileName = "backup-collections.json";
    internal const int FormatVersion = 1;

    /// <summary>The collection <paramref name="backup"/> was taken from, when it is one of the backups in <paramref name="backupsDir"/> and the index names it.</summary>
    public static string? CollectionOf(string backup, string backupsDir)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(backup));
        if (parent is null || !SameFolder(parent, backupsDir))
        {
            return null;
        }
        return Load(backupsDir).TryGetValue(Path.GetFileName(backup), out var collection) ? collection : null;
    }

    /// <summary>
    /// Records <paramref name="collection"/> for <paramref name="backup"/>, dropping entries whose
    /// backup has since been pruned. An unchanged file keeps its one backup, which then belongs to
    /// the collection that wrote it last: its bytes are that collection's as much as the earlier one's.
    /// </summary>
    public static void Record(string collection, string backup, string backupsDir)
    {
        var loaded = Load(backupsDir);
        var index = loaded
            .Where(p => File.Exists(Path.Combine(backupsDir, p.Key)))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        index[Path.GetFileName(backup)] = collection;
        if (DictionaryEquality.Equal(index, loaded))
        {
            return;
        }
        Save(index, backupsDir);
    }

    /// <summary>Renames <paramref name="collection"/> wherever the index records it, so a backup taken before a rename still restores into the collection it came from.</summary>
    public static void Rename(string collection, string newName, string backupsDir)
    {
        var loaded = Load(backupsDir);
        var index = loaded.ToDictionary(p => p.Key, p => p.Value == collection ? newName : p.Value, StringComparer.Ordinal);
        if (DictionaryEquality.Equal(index, loaded))
        {
            return;
        }
        Save(index, backupsDir);
    }

    private static void Save(IReadOnlyDictionary<string, string> index, string backupsDir)
    {
        var root = JsonValue.Object(
            ("version", JsonValue.Int(FormatVersion)),
            ("backups", JsonValue.FromObject(index)));
        AtomicFile.Write(root.Serialize(), Path.Combine(backupsDir, FileName));
    }

    /// <summary>A missing or unreadable index names nothing: a restore then falls back to the active collection, as every restore did before the index was kept.</summary>
    internal static Dictionary<string, string> Load(string backupsDir)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonValue root;
        try
        {
            root = JsonValue.Parse(File.ReadAllBytes(Path.Combine(backupsDir, FileName)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return index;
        }
        if (root.Kind != JsonKind.Object
            || root["version"] is not { Kind: JsonKind.Int } version || version.IntValue != FormatVersion
            || root["backups"] is not { Kind: JsonKind.Object } backups)
        {
            return index;
        }
        foreach (var (name, value) in backups.ObjectProperties)
        {
            if (value.Kind == JsonKind.String)
            {
                index[name] = value.StringValue;
            }
        }
        return index;
    }

    private static bool SameFolder(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

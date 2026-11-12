namespace ConnectorControl.Core;

/// <summary>
/// Which backups are kept past the keep count. The master-list backup taken just before a delete
/// is the only copy of what the delete removed, and the count-based pruning rotates it out within
/// a day of normal use, so <see cref="BackupManager"/> keeps a pinned backup for
/// <see cref="BackupManager.PinDays"/>. The pins are one small index beside the backups, so every
/// backup stays a byte copy of its file; a pin names the backup by file name.
///
/// The name is chosen so no backup series lists it: every series is matched by "&lt;series&gt;.".
///
/// Mirror: Sources/ConnectorControlCore/BackupPins.swift
/// </summary>
public static class BackupPins
{
    public const string FileName = "backup-pins.json";
    internal const int FormatVersion = 1;

    /// <summary>Pins <paramref name="backup"/>, dropping pins whose backup has since been pruned. Saves only when the index changes, so pinning the backup an unchanged file kept rewrites nothing.</summary>
    public static void Pin(string backup, string backupsDir)
    {
        var loaded = Pinned(backupsDir);
        var index = new HashSet<string>(
            loaded.Where(name => File.Exists(Path.Combine(backupsDir, name))), StringComparer.Ordinal)
        {
            Path.GetFileName(backup),
        };
        if (index.SetEquals(loaded))
        {
            return;
        }
        Save(index, backupsDir);
    }

    /// <summary>The pinned backups' file names. A missing or unreadable index pins nothing.</summary>
    public static HashSet<string> Pinned(string backupsDir)
    {
        var pins = new HashSet<string>(StringComparer.Ordinal);
        JsonValue root;
        try
        {
            root = JsonValue.Parse(File.ReadAllBytes(Path.Combine(backupsDir, FileName)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return pins;
        }
        if (root.Kind != JsonKind.Object
            || root["version"] is not { Kind: JsonKind.Int } version || version.IntValue != FormatVersion
            || root["pins"] is not { Kind: JsonKind.Array } items)
        {
            return pins;
        }
        foreach (var item in items.ArrayItems)
        {
            if (item.Kind == JsonKind.String)
            {
                pins.Add(item.StringValue);
            }
        }
        return pins;
    }

    private static void Save(IEnumerable<string> index, string backupsDir)
    {
        var root = JsonValue.Object(
            ("version", JsonValue.Int(FormatVersion)),
            ("pins", JsonValue.Array(index.Order(StringComparer.Ordinal).Select(JsonValue.String))));
        AtomicFile.Write(root.Serialize(), Path.Combine(backupsDir, FileName));
    }
}

using System.Globalization;
using System.Text.Json;

namespace ConnectorControl.Core.Services;

/// <summary>
/// settings.json in the machine-local data folder (spec §6.5): a flat JSON
/// object written in the Apple encoder format, unknown keys preserved,
/// wrong-typed values ignored, corrupt file treated as empty until the next write.
/// </summary>
public sealed class SettingsStore : ISettings
{
    private readonly string path;
    private JsonValue root = JsonValue.Object();

    public SettingsStore(string path)
    {
        this.path = path;
        Reload();
    }

    public void Reload()
    {
        try
        {
            if (File.Exists(path))
            {
                var parsed = JsonValue.Parse(File.ReadAllBytes(path));
                root = parsed.Kind == JsonKind.Object ? parsed : JsonValue.Object();
                return;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // fall through: behave like a missing file
        }
        root = JsonValue.Object();
    }

    public string? MasterStoreDir { get => GetString("masterStoreDir"); set => SetString("masterStoreDir", value); }
    public string? ClaudeConfigPath { get => GetString("claudeConfigPath"); set => SetString("claudeConfigPath", value); }
    public string? ClaudeLaunchTarget { get => GetString("claudeLaunchTarget"); set => SetString("claudeLaunchTarget", value); }
    public int BackupKeepCount { get => GetInt("backupKeepCount", 20); set => Set("backupKeepCount", JsonValue.Int(value)); }
    public bool NotifyExternalChanges { get => GetBool("notifyExternalChanges", true); set => Set("notifyExternalChanges", JsonValue.Bool(value)); }
    public bool ConfirmBeforeRestart { get => GetBool("confirmBeforeRestart", true); set => Set("confirmBeforeRestart", JsonValue.Bool(value)); }
    public bool ConfirmBeforeQuit { get => GetBool("confirmBeforeQuit", true); set => Set("confirmBeforeQuit", JsonValue.Bool(value)); }
    public bool AclSweepDone { get => GetBool("aclSweepDone", false); set => Set("aclSweepDone", JsonValue.Bool(value)); }
    public bool AutoUpdate { get => GetBool("autoUpdate", true); set => Set("autoUpdate", JsonValue.Bool(value)); }
    public bool TrayTipShown { get => GetBool("trayTipShown", false); set => Set("trayTipShown", JsonValue.Bool(value)); }

    public DateTime? LastApplyDate
    {
        get
        {
            var raw = GetString("lastApplyDate");
            if (raw is not null && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
            {
                return DateTime.SpecifyKind(date, DateTimeKind.Utc);
            }
            return null;
        }
        set => SetString("lastApplyDate", value?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
    }

    private string? GetString(string key) => root[key] is { Kind: JsonKind.String } v ? v.StringValue : null;

    private int GetInt(string key, int fallback) =>
        root[key] is { Kind: JsonKind.Int } v && v.IntValue is >= int.MinValue and <= int.MaxValue ? (int)v.IntValue : fallback;

    private bool GetBool(string key, bool fallback) => root[key] is { Kind: JsonKind.Bool } v ? v.BoolValue : fallback;

    private void SetString(string key, string? value)
    {
        if (value is null)
        {
            root = root.Without(key);
            Save();
        }
        else
        {
            Set(key, JsonValue.String(value));
        }
    }

    private void Set(string key, JsonValue value)
    {
        root = root.With(key, value);
        Save();
    }

    private void Save() => AtomicFile.Write(root.Serialize(), path);
}

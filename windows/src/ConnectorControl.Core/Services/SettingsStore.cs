using System.Globalization;
using System.Text.Json;

namespace ConnectorControl.Core.Services;

/// <summary>
/// The one list of keys <see cref="SettingsStore"/> uses, so the accessors and their tests cannot
/// disagree about what a setting is called. The strings are what users' settings files hold, so they
/// never change. Mirror: DefaultsKey (Sources/ConnectorControlCore/DefaultsKey.swift).
/// </summary>
internal static class SettingsKey
{
    public const string MasterStoreDir = "masterStoreDir";
    public const string ClaudeConfigPath = "claudeConfigPath";
    public const string ClaudeLaunchTarget = "claudeLaunchTarget";
    public const string BackupKeepCount = "backupKeepCount";
    public const string NotifyExternalChanges = "notifyExternalChanges";
    public const string ConfirmBeforeRestart = "confirmBeforeRestart";
    public const string ConfirmBeforeQuit = "confirmBeforeQuit";
    public const string ClaudeIntegrationEnabled = "claudeIntegrationEnabled";
    public const string ConfirmClaudeChanges = "confirmClaudeChanges";
    public const string ClaudeIntegrationOfferAnswered = "claudeIntegrationOfferAnswered";
    public const string SweepVersion = "sweepVersion";
    public const string AutoUpdate = "autoUpdate";
    public const string TrayTipShown = "trayTipShown";
    public const string DeclinedUpdateVersion = "declinedUpdateVersion";
    public const string LastApplyDate = "lastApplyDate";
}

/// <summary>
/// settings.json in the machine-local data folder: a flat JSON object written in the Apple
/// encoder format, unknown keys preserved, wrong-typed values ignored, corrupt file treated as
/// empty until the next write.
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

    internal void Reload()
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

    public string? MasterStoreDir { get => GetString(SettingsKey.MasterStoreDir); set => SetString(SettingsKey.MasterStoreDir, value); }
    public string? ClaudeConfigPath { get => GetString(SettingsKey.ClaudeConfigPath); set => SetString(SettingsKey.ClaudeConfigPath, value); }
    public string? ClaudeLaunchTarget { get => GetString(SettingsKey.ClaudeLaunchTarget); set => SetString(SettingsKey.ClaudeLaunchTarget, value); }
    public int BackupKeepCount { get => GetInt(SettingsKey.BackupKeepCount, BackupManager.DefaultKeepCount); set => Set(SettingsKey.BackupKeepCount, JsonValue.Int(value)); }
    public bool NotifyExternalChanges { get => GetBool(SettingsKey.NotifyExternalChanges, true); set => Set(SettingsKey.NotifyExternalChanges, JsonValue.Bool(value)); }
    public bool ConfirmBeforeRestart { get => GetBool(SettingsKey.ConfirmBeforeRestart, true); set => Set(SettingsKey.ConfirmBeforeRestart, JsonValue.Bool(value)); }
    public bool ConfirmBeforeQuit { get => GetBool(SettingsKey.ConfirmBeforeQuit, true); set => Set(SettingsKey.ConfirmBeforeQuit, JsonValue.Bool(value)); }
    public bool ClaudeIntegrationEnabled { get => GetBool(SettingsKey.ClaudeIntegrationEnabled, false); set => Set(SettingsKey.ClaudeIntegrationEnabled, JsonValue.Bool(value)); }
    public bool ConfirmClaudeChanges { get => GetBool(SettingsKey.ConfirmClaudeChanges, true); set => Set(SettingsKey.ConfirmClaudeChanges, JsonValue.Bool(value)); }
    public bool ClaudeIntegrationOfferAnswered { get => GetBool(SettingsKey.ClaudeIntegrationOfferAnswered, false); set => Set(SettingsKey.ClaudeIntegrationOfferAnswered, JsonValue.Bool(value)); }
    public int SweepVersion { get => GetInt(SettingsKey.SweepVersion, 0); set => Set(SettingsKey.SweepVersion, JsonValue.Int(value)); }
    public bool AutoUpdate { get => GetBool(SettingsKey.AutoUpdate, false); set => Set(SettingsKey.AutoUpdate, JsonValue.Bool(value)); }
    public bool TrayTipShown { get => GetBool(SettingsKey.TrayTipShown, false); set => Set(SettingsKey.TrayTipShown, JsonValue.Bool(value)); }
    public string? DeclinedUpdateVersion { get => GetString(SettingsKey.DeclinedUpdateVersion); set => SetString(SettingsKey.DeclinedUpdateVersion, value); }

    public string? LastSaveError { get; private set; }

    public DateTime? LastApplyDate
    {
        get
        {
            var raw = GetString(SettingsKey.LastApplyDate);
            if (raw is not null && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
            {
                return DateTime.SpecifyKind(date, DateTimeKind.Utc);
            }
            return null;
        }
        set => SetString(SettingsKey.LastApplyDate, value?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
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

    /// <summary>
    /// Best-effort: a write failure (unwritable directory, locked file, …)
    /// must not surface as an exception, because the Mac's UserDefaults
    /// setters never fail and callers (e.g. recording a successful apply)
    /// rely on that. The in-memory value is kept either way.
    /// </summary>
    private void Save()
    {
        try
        {
            AtomicFile.Write(root.Serialize(), path);
            LastSaveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastSaveError = ex.Message;
        }
    }
}

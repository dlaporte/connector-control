using System.Text.Json;

namespace ConnectorControl.Core.State;

/// <summary>Catalog §5 RestoreSheetView state.</summary>
public sealed class RestoreModel : ObservableObject
{
    public const string Headline = "Restore Claude config from a backup";
    public const string Caption = "The current file is backed up first, then replaced by the selected backup.";
    public const string CancelTitle = "Cancel";
    public const string RestoreTitle = "Restore…";
    public const string RestoreButton = "Restore";
    private const string Series = "claude_desktop_config";

    private readonly AppState state;
    private readonly IDialogs dialogs;
    private IReadOnlyList<string> backups = [];
    private string? selection;
    private string? restoreError;

    public RestoreModel(AppState state, IDialogs dialogs)
    {
        this.state = state;
        this.dialogs = dialogs;
    }

    public event Action? CloseRequested;

    /// <summary>Full paths, newest first; the permanent .original snapshot last (catalog §5).</summary>
    public IReadOnlyList<string> Backups => backups;

    public IReadOnlyList<string> BackupNames => backups.Select(Path.GetFileName).Select(n => n ?? "").ToList();

    public string? Selection
    {
        get => selection;
        set
        {
            if (Set(ref selection, value))
            {
                Raise(nameof(CanRestore));
            }
        }
    }

    public bool CanRestore => selection is not null;

    public string? RestoreError
    {
        get => restoreError;
        private set
        {
            if (Set(ref restoreError, value))
            {
                Raise(nameof(HasRestoreError));
            }
        }
    }

    public bool HasRestoreError => restoreError is not null;

    public void Load()
    {
        var found = new List<string>(state.Service.Backups.Backups(Series));
        var original = Path.Combine(state.Service.Backups.BackupsDir, $"{Series}.original.json");
        if (File.Exists(original))
        {
            found.Add(original);
        }
        backups = found;
        Raise(nameof(Backups));
        Raise(nameof(BackupNames));
    }

    public void Cancel() => CloseRequested?.Invoke();

    /// <summary>Confirm, then restore through AppState (which syncs the baseline). True when restored and closing.</summary>
    public bool Restore()
    {
        RestoreError = null;   // a fresh attempt starts with a clean sheet (catalog §5 shows the error only after a failure)
        if (selection is not { } backup)
        {
            return false;
        }
        if (!dialogs.Confirm($"Replace Claude's config with {Path.GetFileName(backup)}?", null, RestoreButton, destructive: true))
        {
            return false;
        }
        try
        {
            state.RestoreClaudeConfig(backup);
            CloseRequested?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is ClaudeConfigException or IOException or UnauthorizedAccessException or JsonException)
        {
            RestoreError = ex.Message;         // raw message, not Friendly(): catalog §5
            state.LastError = ex.Message;
            return false;
        }
    }
}

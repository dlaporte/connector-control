using System.Text.Json;

namespace ConnectorControl.Core.State;

/// <summary>The Restore dialog's Claude's config tab, and the dialog's own strings.</summary>
public sealed class RestoreModel : ObservableObject
{
    public const string Headline = "Restore from a backup";
    /// <summary>The dialog's two tabs, Collections first and chosen when it opens.</summary>
    public const string CollectionsTab = "Collections";
    public const string ClaudeConfigTab = "Claude’s config";
    public const string Caption = "The current file is backed up first, then replaced by the selected backup.";
    /// <summary>Under the Claude's config tab's caption.</summary>
    public const string ClaudeConfigNote = "It holds only the connectors that were on. To bring back a whole collection, use Collections.";
    public const string CancelTitle = "Cancel";
    /// <summary>
    /// The button that opens this dialog from Settings and the dialog's own Restore, on both
    /// platforms; <see cref="RestoreButton"/> is the confirmation's, a separate question with its
    /// own title.
    /// </summary>
    public const string RestoreTitle = "Restore";
    public const string RestoreButton = "Restore";
    public const string Series = "claude_desktop_config";

    /// <summary>The first-run original's row: it has no time to be named by.</summary>
    public const string OriginalLabel = "First-run original";
    /// <summary>The confirmation names what was chosen: the backup's time, or the first-run original.</summary>
    public static string ConfirmMessage(string label) => $"Replace Claude’s config with the backup from {label}?";
    public const string ConfirmOriginalMessage = "Replace Claude’s config with the first-run original?";

    /// <summary>The confirmation's informative text when the backup's collection is gone.</summary>
    public static string RecreateInformative(string collection) => $"“{collection}” no longer exists, so it is created again with only the connectors Claude was running. To bring back all of its connectors, restore it from Collections.";

    private readonly AppState state;
    private readonly IDialogs dialogs;
    private IReadOnlyList<string> backups = [];
    /// <summary>Each listed backup's row, by file name, as <see cref="Load"/> named them.</summary>
    private Dictionary<string, string> labels = new(StringComparer.Ordinal);
    /// <summary>The first-run original's file name, when it was listed.</summary>
    private string? original;
    private string? selection;
    private string? restoreError;

    public RestoreModel(AppState state, IDialogs dialogs)
    {
        this.state = state;
        this.dialogs = dialogs;
    }

    public event Action? CloseRequested;

    /// <summary>Full paths, newest first; the permanent .original snapshot last.</summary>
    public IReadOnlyList<string> Backups => backups;

    /// <summary>The rows, in <see cref="Backups"/>' order: what the list shows and a screen reader says.</summary>
    public IReadOnlyList<string> BackupLabels => backups.Select(Label).ToList();

    /// <summary>A backup's row: its time, as <see cref="BackupManager.Labels"/> names it among the backups listed (the
    /// Collections tab's rule), or <see cref="OriginalLabel"/> for the first-run original.</summary>
    public string Label(string backup)
    {
        var name = Path.GetFileName(backup);
        return labels.GetValueOrDefault(name) ?? name;
    }

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
        var found = new List<string>(state.Service.Backups.RestorableBackups(Series));
        // The first-run original by its own path, not by having no time: a stray name without one is its own
        // label, as on the Collections tab.
        original = state.Service.Backups.OriginalSnapshotPath(Series) is { } path ? Path.GetFileName(path) : null;
        labels = new Dictionary<string, string>(BackupManager.Labels(found), StringComparer.Ordinal);
        if (original is not null)
        {
            labels[original] = OriginalLabel;
        }
        backups = found;
        Raise(nameof(Backups));
        Raise(nameof(BackupLabels));
    }

    public void Cancel() => CloseRequested?.Invoke();

    /// <summary>Confirm, then restore through AppState (which syncs the baseline). True when restored and closing.</summary>
    public bool Restore()
    {
        RestoreError = null;   // a fresh attempt starts with a clean sheet; the error shows only after a failure
        if (selection is not { } backup)
        {
            return false;
        }
        // A restore AppState would refuse says why at once, rather than after a confirmation it
        // could never honour.
        var recorded = BackupCollections.CollectionOf(backup, state.Service.Paths.BackupsDir);
        if (state.RestoreRefusalForRecorded(recorded) is { } refusal)
        {
            RestoreError = refusal.Message;
            return false;
        }
        // What the confirmation is asked about: the backup's record and content as read here, so what
        // is restored is the backup as it was when the user was asked. One that cannot be read says so
        // at once, as a refusal does: there is nothing to ask about.
        byte[] data;
        try
        {
            data = BoundedRead.Data(backup, ConfigService.MaxBackupBytes);
        }
        // Platform idiom: the I/O and access errors a read throws; the Mac mirror's catch takes every error.
        catch (Exception ex) when (FileSystemErrors.IsTransient(ex))
        {
            RestoreError = ex.Message;
            return false;
        }
        // Under the message: when the collection the backup records is gone, that the restore creates it
        // again with only what Claude was running.
        var informative = recorded is not null && state.Store.StoredCollectionName(recorded) is null ? RecreateInformative(recorded) : null;
        var message = Path.GetFileName(backup) == original ? ConfirmOriginalMessage : ConfirmMessage(Label(backup));
        if (!dialogs.Confirm(message, informative, RestoreButton, destructive: true))
        {
            return false;
        }
        try
        {
            state.RestoreClaudeConfig(backup, data, recorded);
            CloseRequested?.Invoke();
            return true;
        }
        catch (RestoreSubscribedException ex)
        {
            // A refusal is the dialog's alone, as the one before the confirmation is: nothing was
            // restored, so there is nothing for the banner to say. It arrives here only when the
            // collections changed while the confirmation was up.
            RestoreError = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is ClaudeConfigException or IOException or UnauthorizedAccessException or JsonException)
        {
            // A restore that failed is the banner's too.
            RestoreError = ex.Message;         // raw message, not Friendly()
            state.LastError = ex.Message;
            return false;
        }
    }
}

namespace ConnectorControl.Core.State;

/// <summary>
/// The Restore dialog's Collections tab: the master list's backups, the collections the chosen one holds
/// and how each stands against the store now, and the restore of one of them as it was then. Every backup
/// is read once, when the tab loads, and a restore restores the bytes read then, so what the tab shows is
/// what it restores. The lines a restore's confirmation lists, one per connector it changes
/// (<see cref="CollectionRestorePlan.Lines"/>), are <c>restore_collection</c>'s too; each takes the name as
/// its caller shows it.
///
/// Mirror: Sources/ConnectorControlState/CollectionRestoreModel.swift
/// </summary>
public sealed class CollectionRestoreModel : ObservableObject
{
    /// <summary>The master list's backups, as <c>ConfigService.SaveStore</c> names them: the ones this tab and
    /// <c>list_backups</c> list, and <c>restore_collection</c> takes.</summary>
    public const string Series = "mcps";

    public const string Caption = "Choose a backup, then a collection. The collection goes back to exactly how it was; its current connectors are backed up first and kept for 30 days.";
    public const string GoneState = "deleted since";
    public const string SameState = "same as now";
    public const string DiffersState = "differs from now";
    public const string SubscribedState = "subscribed now";
    /// <summary>Joins a collection row's connector count and its state, and the row's name to that detail where
    /// the row is spoken.</summary>
    public const string DetailSeparator = " · ";
    public const string ActiveInformative = "Claude is running this collection, so Claude’s config is updated too.";

    public static string ConfirmMessage(string collection, string time) => $"Restore “{collection}” as it was at {time}?";
    public static string CreatedInformative(string collection) => $"“{collection}” no longer exists, so it is created again.";

    public static string ComesBackLine(string name, bool on) => on ? $"+ {name} (on)" : $"+ {name} (off)";
    public static string ChangesLine(string name, bool on) => on ? $"~ {name} (on)" : $"~ {name} (off)";
    /// <summary>"−" is U+2212, as <c>ControlMasking.RemovedPrefix</c> marks a removed line.</summary>
    public static string GoesLine(string name) => $"− {name}";
    public static string UnchangedLine(int count) => $"= {count} unchanged";

    /// <summary>Under the confirmation's message: the plan's lines, one per line, then, after a blank line, that a
    /// gone collection is created again or that the active one updates Claude's config too. A gone collection
    /// that held no connectors has no lines, and says only that.</summary>
    public static string Informative(CollectionRestorePlan plan)
    {
        var after = plan.Created ? CreatedInformative(plan.Collection) : plan.Active ? ActiveInformative : null;
        return string.Join("\n\n", new[] { string.Join("\n", plan.Lines()), after ?? "" }.Where(part => part.Length > 0));
    }

    /// <summary>How a collection in a backup stands against the store now.</summary>
    public enum RowState
    {
        /// <summary>The store has no such collection: a restore creates it again.</summary>
        Gone,
        /// <summary>It is already as the backup holds it.</summary>
        Same,
        /// <summary>A restore would change it.</summary>
        Differs,
        /// <summary>It is subscribed now: its connectors are its author's.</summary>
        Subscribed,
    }

    public static string StateText(RowState state) => state switch
    {
        RowState.Gone => GoneState,
        RowState.Same => SameState,
        RowState.Differs => DiffersState,
        _ => SubscribedState,
    };

    /// <summary>Only a collection a restore would change can be restored.</summary>
    public static bool IsRestorable(RowState state) => state is RowState.Gone or RowState.Differs;

    /// <summary>A master-list backup as the tab read it when it loaded: its full path; its time, as
    /// <see cref="BackupManager.Labels"/> names it among the backups listed; its bytes as read then, null when
    /// they could not be, which are what a restore from it restores; and those bytes decoded, null when they
    /// could not be read or decoded.</summary>
    public sealed record Backup(string Path, string Label, byte[]? Data, MasterStore? Store);

    /// <summary>One of the chosen backup's collections: its name as the backup holds it, how it stands now, and
    /// the detail shown beside the name, its connector count and state.</summary>
    public sealed record Row(string Name, RowState State, string Detail)
    {
        /// <summary>What a screen reader says for the row: the name, then the detail.</summary>
        public string SpokenName => Name + DetailSeparator + Detail;
    }

    private readonly AppState state;
    private readonly IDialogs dialogs;
    private IReadOnlyList<Backup> backups = [];
    private string? selection;
    private IReadOnlyList<Row> rows = [];
    private string? collection;
    private string? restoreError;

    public CollectionRestoreModel(AppState state, IDialogs dialogs)
    {
        this.state = state;
        this.dialogs = dialogs;
    }

    public event Action? CloseRequested;

    /// <summary>Newest first; the series has no first-run original.</summary>
    public IReadOnlyList<Backup> Backups => backups;

    public IReadOnlyList<string> BackupLabels => backups.Select(b => b.Label).ToList();

    /// <summary>The chosen backup's full path, none when the tab opens. Choosing another clears the chosen
    /// collection and lists the new one's collections; one that could not be read says so, and lists none.</summary>
    public string? Selection
    {
        get => selection;
        set
        {
            if (!Set(ref selection, value))
            {
                return;
            }
            Collection = null;
            var backup = SelectedBackup;
            RestoreError = backup is { Store: null } ? AppState.CollectionBackupUnreadableError(Path.GetFileName(backup.Path)) : null;
            Rows = backup is null ? [] : RowsOf(backup);
        }
    }

    /// <summary>The chosen backup's collections, by name ordinally.</summary>
    public IReadOnlyList<Row> Rows
    {
        get => rows;
        private set
        {
            rows = value;
            Raise();
            Raise(nameof(CanRestore));
        }
    }

    /// <summary>The chosen collection, by its row's name. Choosing one re-reads the chosen backup's rows, so a row
    /// that changed since the backup was chosen shows how it stands now.</summary>
    public string? Collection
    {
        get => collection;
        set
        {
            if (Set(ref collection, value))
            {
                if (value is not null && SelectedBackup is { } backup)
                {
                    Rows = RowsOf(backup);
                }
                Raise(nameof(CanRestore));
            }
        }
    }

    /// <summary>Only a collection that is deleted since or differs from now can be restored.</summary>
    public bool CanRestore => collection is not null
        && rows.FirstOrDefault(row => string.Equals(row.Name, collection, StringComparison.Ordinal)) is { } row && IsRestorable(row.State);

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

    private Backup? SelectedBackup => backups.FirstOrDefault(b => string.Equals(b.Path, selection, StringComparison.Ordinal));

    /// <summary>Lists the master list's backups and reads each one once, within the bound every backup read keeps,
    /// as <c>list_backups</c> reads them.</summary>
    public void Load()
    {
        IReadOnlyList<string> paths;
        try
        {
            paths = state.Service.Backups.Backups(Series);
        }
        // Platform idiom: the I/O and access errors a listing throws; the Mac mirror's catch takes every error.
        catch (Exception ex) when (FileSystemErrors.IsTransient(ex))
        {
            RestoreError = ex.Message;
            return;
        }
        var labels = BackupManager.Labels(paths);
        backups = paths.Select(path =>
        {
            byte[]? data = null;
            MasterStore? store = null;
            try
            {
                data = BoundedRead.Data(path, ConfigService.MaxBackupBytes);
                store = MasterStoreIO.Decode(data);
            }
            catch (Exception ex) when (ControlTools.IsBackupError(ex))
            {
                // Platform idiom: whatever the read or the decode throws for the backup itself, as the Mac's try?
                // takes every error they throw; a programming fault is left to end the call, as list_backups
                // leaves it. The backup is listed, and says it can't be read when chosen.
            }
            var name = Path.GetFileName(path);
            return new Backup(path, labels.GetValueOrDefault(name) ?? name, data, store);
        }).ToList();
        Raise(nameof(Backups));
        Raise(nameof(BackupLabels));
    }

    public void Cancel() => CloseRequested?.Invoke();

    /// <summary>The Restore button: confirm, then restore the bytes the tab read through AppState. A restore
    /// AppState would refuse says why at once, rather than after a confirmation it could never honour. True when
    /// restored and closing.</summary>
    public bool Restore()
    {
        if (SelectedBackup is not { } backup || collection is not { } chosen)
        {
            return false;
        }
        RestoreError = null;   // a fresh attempt starts with a clean sheet; the error shows only after a failure
        var id = Path.GetFileName(backup.Path);
        CollectionRestorePlan plan;
        try
        {
            plan = state.CollectionRestorePlan(chosen, id, backup.Data);
        }
        catch (CollectionRestoreException refusal)
        {
            Refused(refusal, backup);
            return false;
        }
        if (!dialogs.Confirm(ConfirmMessage(plan.Collection, backup.Label), Informative(plan), RestoreModel.RestoreButton, destructive: true))
        {
            return false;
        }
        try
        {
            // Never null here: bytes that could not be read give no plan, and none would restore anyway.
            state.RestoreCollection(chosen, backup.Path, backup.Data ?? []);
            CloseRequested?.Invoke();
            return true;
        }
        catch (CollectionRestoreException refusal)
        {
            // A refusal is the dialog's alone, as the one before the confirmation is: nothing was restored,
            // so there is nothing for the banner to say. It arrives here only when the collection changed
            // while the confirmation was up.
            Refused(refusal, backup);
            return false;
        }
        // Platform idiom: the restore throws only its refusals, and its save reports its own failures; an I/O
        // or access error is the banner's too. The Mac mirror's catch must take every error.
        catch (Exception ex) when (FileSystemErrors.IsTransient(ex))
        {
            // A restore that failed is the banner's too.
            RestoreError = ex.Message;         // raw message, not Friendly()
            state.LastError = ex.Message;
            return false;
        }
    }

    /// <summary>A refusal shows on the dialog's failure line, and the rows say how the collections stand now.</summary>
    private void Refused(CollectionRestoreException refusal, Backup backup)
    {
        RestoreError = refusal.Message;
        Rows = RowsOf(backup);
    }

    /// <summary>The backup's collections by name ordinally, each with how it stands now: the plan the restore would
    /// follow, or the refusal that would stop it.</summary>
    private List<Row> RowsOf(Backup backup)
    {
        if (backup.Store is not { } store)
        {
            return [];
        }
        var id = Path.GetFileName(backup.Path);
        return store.Collections.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
        {
            RowState rowState;
            try
            {
                rowState = state.CollectionRestorePlan(p.Key, id, backup.Data).Created ? RowState.Gone : RowState.Differs;
            }
            catch (RestoreCollectionSubscribedException)
            {
                rowState = RowState.Subscribed;
            }
            catch (RestoreCollectionUnchangedException)
            {
                rowState = RowState.Same;
            }
            catch (CollectionRestoreException)
            {
                // The backup's own collection in bytes it decoded: no other refusal reaches here. Asked to restore
                // it, the refusal says why.
                rowState = RowState.Differs;
            }
            var count = ControlTools.ConnectorCountText(p.Value.Mcps.Count, p.Value.Mcps.Count(m => m.Value.Enabled));
            return new Row(p.Key, rowState, count + DetailSeparator + StateText(rowState));
        }).ToList();
    }
}

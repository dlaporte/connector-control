using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// <c>restore_collection</c>: one collection put back from a master-list backup, through the gate.
///
/// Mirror: Tests/ConnectorControlStateTests/ControlToolsCollectionRestoreTests.swift
/// </summary>
public class ControlToolsCollectionRestoreTests
{
    private const string Secret = "s3cr3t-notes-token";

    private static ControlRig Rig(bool? answer = true)
    {
        var rig = new ControlRig();
        rig.Confirmer.Scripted = answer;
        return rig;
    }

    private static JsonValue Config(string command) => JsonValue.Object(("command", JsonValue.String(command)));

    private static JsonValue Arguments(string id, string collection) =>
        JsonValue.Object(("id", JsonValue.String(id)), ("collection", JsonValue.String(collection)));

    /// <summary>The master list's backups, newest first, as <c>list_backups</c> lists them.</summary>
    private static IReadOnlyList<string> Backups(ControlRig rig) => rig.State.Service.Backups.Backups(CollectionRestoreModel.Series);

    /// <summary><paramref name="store"/> saved where the backups are, as a master-list backup holds it, under a name
    /// older than any backup a save takes. Returns its id.</summary>
    private static string Backup(ControlRig rig, MasterStore store, string name = "mcps.2026-01-01T00-00-00-000Z.json")
    {
        MasterStoreIO.Save(store, Path.Combine(rig.H.BackupsDir, name));
        return name;
    }

    /// <summary>What the question calls the backup <paramref name="id"/>: its label among the backups listed now,
    /// as the Restore dialog labels it. Local time, so never written out.</summary>
    private static string Label(ControlRig rig, string id) => BackupManager.Labels(Backups(rig))[id];

    /// <summary>Every file in the backups folder and its bytes.</summary>
    private static Dictionary<string, byte[]> BackupFiles(ControlRig rig) =>
        Directory.GetFiles(rig.H.BackupsDir).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.Ordinal);

    /// <summary>Personal, two connectors and one of them on (holding the secret), deleted; the id of the backup the
    /// delete took, which holds it whole.</summary>
    private static (string Id, Collection Personal) DeletedPersonal(ControlRig rig)
    {
        Assert.Null(rig.State.AddEmptyCollection("Personal"));
        var notes = new McpEntry(JsonValue.Object(("command", JsonValue.String("node")),
            ("env", JsonValue.Object(("NOTES_TOKEN", JsonValue.String(Secret))))));
        Assert.Null(rig.State.Upsert("notes", notes, null, "Personal"));
        Assert.Null(rig.State.Upsert("calendar", new McpEntry(false, Config("calendar")), null, "Personal"));
        var personal = rig.State.Store.Collections["Personal"].Clone();
        Assert.Null(rig.State.DeleteCollection("Personal"));
        var taken = Backups(rig).First(path => MasterStoreIO.Read(path)?.Collections.GetValueOrDefault("Personal")?.Equals(personal) == true);
        return (Path.GetFileName(taken), personal);
    }

    /// <summary>A deleted collection: the question names the backup by its label and lists every connector that
    /// comes back with its switch; allowed, the collection is back exactly as it was, and the answer carries no
    /// config value.</summary>
    [Fact]
    public void RestoringADeletedCollectionAsksWithEveryLineAndCreatesItAgain()
    {
        using var rig = Rig();
        var (id, personal) = DeletedPersonal(rig);
        var time = Label(rig, id);
        var result = rig.Call("restore_collection", Arguments(id, "Personal"));
        Assert.NotNull(result);
        Assert.Equal([new ConfirmationRequest(
            ControlTools.RestoreTitle("Claude", "Personal"),
            ControlTools.RestoreCollectionCreatedText("Personal", time),
            ["+ calendar (off)", "+ notes (on)"], null)], rig.Confirmer.Requests);
        Assert.Equal(new ControlResult(JsonValue.Object(
            ("id", JsonValue.String(id)), ("collection", JsonValue.String("Personal")), ("activeCollection", JsonValue.String("Default")),
            ("restartRequired", JsonValue.Bool(false)), ("applied", JsonValue.Bool(true))),
            ControlTools.RestoredText("Personal"), false), result);
        Assert.Equal(personal, rig.State.Store.Collections["Personal"]);
        Assert.False(rig.State.IsSynced("Personal"));
        Assert.Equal("Default", rig.State.ActiveCollection);
        Assert.DoesNotContain(Secret, result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.Structured.CompactLine(), StringComparison.Ordinal);
    }

    /// <summary>Each refusal comes before anyone is asked: an id that is not a master-list backup's (a Claude's
    /// config backup's among them, as restore_backup refuses a master-list backup's), one that cannot be read, a
    /// collection the backup does not hold, one subscribed now, and one already as it was. The names they echo
    /// show as escapes.</summary>
    [Fact]
    public void EachRefusalComesBeforeAnyoneIsAsked()
    {
        using var rig = Rig();
        rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam);
        var id = Backup(rig, rig.State.Store);
        const string unreadable = "mcps.2026-01-02T00-00-00-000Z.json";
        File.WriteAllText(Path.Combine(rig.H.BackupsDir, unreadable), "{");
        var claudeId = Path.GetFileName(rig.State.Service.Backups.Backups(RestoreModel.Series)[0]);
        const string odd = "Odd”\nname";
        (string Tool, JsonValue Arguments, ControlResult Expected)[] cases =
        [
            ("restore_collection", Arguments("../mcps.json", "Default"), ControlResult.Refusal(ControlTools.NoBackupError("../mcps.json"))),
            ("restore_collection", Arguments(claudeId, "Default"), ControlResult.Refusal(ControlTools.NoBackupError(claudeId))),
            ("restore_backup", JsonValue.Object(("id", JsonValue.String(id))), ControlResult.Refusal(ControlTools.NoBackupError(id))),
            ("restore_collection", Arguments(unreadable, "Default"), ControlResult.Refusal(AppState.CollectionBackupUnreadableError(unreadable))),
            ("restore_collection", Arguments(id, odd),
             ControlResult.Refusal(AppState.CollectionNotInBackupError(id, ControlText.EscapedName(odd)))),
            ("restore_collection", Arguments(id, "Data team"), ControlResult.Refusal(AppState.RestoreCollectionSubscribedError("Data team"))),
            ("restore_collection", Arguments(id, "Default"), ControlResult.Refusal(AppState.RestoreCollectionUnchangedError("Default"))),
        ];
        foreach (var (tool, arguments, expected) in cases)
        {
            Assert.Equal(expected, rig.Call(tool, arguments));
        }
        Assert.Empty(rig.Confirmer.Requests);
    }

    /// <summary>Don't Allow leaves the master list, Claude's config and every backup as they were.</summary>
    [Fact]
    public void DontAllowLeavesTheStoreAndTheBackupsUntouched()
    {
        using var rig = Rig(answer: false);
        var (id, _) = DeletedPersonal(rig);
        var store = rig.H.StoreOnDisk();
        var claude = rig.H.ClaudeServers();
        var files = BackupFiles(rig);
        Assert.Equal(ControlResult.Refusal(ControlTools.DeclinedError), rig.Call("restore_collection", Arguments(id, "Personal")));
        Assert.Single(rig.Confirmer.Requests);
        Assert.False(rig.State.Store.Collections.ContainsKey("Personal"));
        Assert.Equal(store, rig.H.StoreOnDisk());
        Assert.Equal(claude, rig.H.ClaudeServers());
        Assert.Equal(files, BackupFiles(rig));
    }

    /// <summary>A config changed while the question was up, where the question reads the same (the connector
    /// changes either way), is not overwritten: the Allow lands nothing.</summary>
    [Fact]
    public void ACollectionThatChangedWhileAskingIsNotRestored()
    {
        using var rig = Rig(answer: null);
        Assert.Null(rig.State.AddEmptyCollection("Work"));
        Assert.Null(rig.State.Upsert("one", new McpEntry(Config("a")), null, "Work"));
        var id = Backup(rig, rig.State.Store);
        Assert.Null(rig.State.Upsert("one", new McpEntry(Config("b")), "one", "Work"));
        var answer = rig.Start("restore_collection", Arguments(id, "Work"));
        Assert.Equal(["~ one (on)"], rig.Confirmer.Requests[^1].Code);
        Assert.Null(rig.State.Upsert("one", new McpEntry(Config("c")), "one", "Work"));
        rig.Confirmer.Answer(true);
        rig.H.Drain();
        Assert.Equal(ControlResult.Refusal(ControlTools.ChangedWhileAskingError), answer.Result);
        // Nothing restored.
        Assert.Equal(Config("c"), rig.State.Store.Collections["Work"].Mcps["one"].Config);
        rig.Start("restore_collection", Arguments(id, "Work"));
        Assert.Equal(2, rig.Confirmer.Requests.Count);
        // The question itself reads the same.
        Assert.Equal(rig.Confirmer.Requests[0], rig.Confirmer.Requests[^1]);
    }

    /// <summary>Only the collection Claude runs takes effect when Claude restarts: its question says so, and its
    /// answer says to restart. Another collection's says neither.</summary>
    [Fact]
    public void OnlyTheActiveCollectionsRestoreSaysToRestart()
    {
        using var rig = Rig();
        Assert.Null(rig.State.AddEmptyCollection("Work"));
        var id = Backup(rig, rig.State.Store);
        Assert.Null(rig.State.Upsert("extra", new McpEntry(Config("x")), null));
        rig.State.ApplyInteractively();
        Assert.Null(rig.State.Upsert("extra", new McpEntry(Config("x")), null, "Work"));
        rig.H.ClaudeRunningSince(hours: 1);
        rig.State.RefreshRestartState();
        var work = rig.Call("restore_collection", Arguments(id, "Work"));
        Assert.NotNull(work);
        Assert.Equal(["− extra"], rig.Confirmer.Requests[^1].Code);
        Assert.Null(rig.Confirmer.Requests[^1].Footer);
        Assert.Equal(ControlTools.RestoreCollectionText("Work", Label(rig, id)), rig.Confirmer.Requests[^1].Message);
        Assert.Equal(ControlTools.RestoredText("Work"), work.Text);
        Assert.Equal(JsonValue.Bool(false), work.Structured.ValueAt(new JsonPointer(["restartRequired"])));
        var active = rig.Call("restore_collection", Arguments(id, "Default"));
        Assert.NotNull(active);
        Assert.Equal(ControlTools.TakesEffectFooter, rig.Confirmer.Requests[^1].Footer);
        Assert.Equal(ControlTools.RestoredText("Default") + " " + ControlTools.RestartToApplyText, active.Text);
        Assert.Equal(JsonValue.Bool(true), active.Structured.ValueAt(new JsonPointer(["restartRequired"])));
        Assert.False(rig.H.ClaudeServers().ContainsKey("extra"));
    }
}

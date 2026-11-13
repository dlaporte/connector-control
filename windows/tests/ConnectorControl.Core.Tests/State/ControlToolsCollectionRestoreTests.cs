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

    /// <summary>The master list's backups and their bytes: a save takes one, and writing Claude's config again takes
    /// only a backup of Claude's config.</summary>
    private static Dictionary<string, byte[]> CollectionBackupFiles(ControlRig rig) =>
        BackupFiles(rig).Where(p => p.Key.StartsWith(CollectionRestoreModel.Series + ".", StringComparison.Ordinal))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    /// <summary>A connector holding a value Claude never sees.</summary>
    private static McpEntry Vault(string value, bool on = true) =>
        new(on, JsonValue.Object(("command", JsonValue.String("node")), ("env", JsonValue.Object(("API_KEY", JsonValue.String(value))))));

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
    /// collection the backup does not hold, one subscribed now, and, from the command line, one already as it was.
    /// The names they echo show as escapes.</summary>
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
        ControlCaller relay = new ControlCaller.Relay("claude-ai");
        (string Tool, JsonValue Arguments, ControlCaller Caller, ControlResult Expected)[] cases =
        [
            ("restore_collection", Arguments("../mcps.json", "Default"), relay, ControlResult.Refusal(ControlTools.NoBackupError("../mcps.json"))),
            ("restore_collection", Arguments(claudeId, "Default"), relay, ControlResult.Refusal(ControlTools.NoBackupError(claudeId))),
            ("restore_backup", JsonValue.Object(("id", JsonValue.String(id))), relay, ControlResult.Refusal(ControlTools.NoBackupError(id))),
            ("restore_collection", Arguments(unreadable, "Default"), relay,
             ControlResult.Refusal(AppState.CollectionBackupUnreadableError(unreadable))),
            ("restore_collection", Arguments(id, odd), relay,
             ControlResult.Refusal(AppState.CollectionNotInBackupError(id, ControlText.EscapedName(odd)))),
            ("restore_collection", Arguments(id, "Data team"), relay,
             ControlResult.Refusal(AppState.RestoreCollectionSubscribedError("Data team"))),
            ("restore_collection", Arguments(id, "Default"), new ControlCaller.Cli(),
             ControlResult.Refusal(AppState.RestoreCollectionUnchangedError("Default"))),
        ];
        foreach (var (tool, arguments, caller, expected) in cases)
        {
            Assert.Equal(expected, rig.Call(tool, arguments, caller));
        }
        Assert.Empty(rig.Confirmer.Requests);
    }

    /// <summary>From Claude, a collection already as the backup holds it is not refused: the question lists its only
    /// line, how many connectors stay as they are, and the answer is a restore's. Nothing in the master list changes
    /// and nothing is saved; a collection Claude doesn't run leaves Claude's config alone too.</summary>
    [Fact]
    public void AnUnchangedCollectionIsAskedAndAnsweredAsRestored()
    {
        using var rig = Rig();
        Assert.Null(rig.State.AddEmptyCollection("Work"));
        Assert.Null(rig.State.Upsert("one", new McpEntry(Config("a")), null, "Work"));
        Assert.Null(rig.State.Upsert("two", new McpEntry(false, Config("b")), null, "Work"));
        var id = Backup(rig, rig.State.Store);
        var time = Label(rig, id);
        var store = rig.H.StoreOnDisk();
        var claude = rig.H.ClaudeServers();
        var files = BackupFiles(rig);
        var result = rig.Call("restore_collection", Arguments(id, "Work"));
        Assert.NotNull(result);
        Assert.Equal([new ConfirmationRequest(
            ControlTools.RestoreTitle("Claude", "Work"),
            ControlTools.RestoreCollectionText("Work", time),
            ["= 2 unchanged"], null)], rig.Confirmer.Requests);
        Assert.Equal(new ControlResult(JsonValue.Object(
            ("id", JsonValue.String(id)), ("collection", JsonValue.String("Work")), ("activeCollection", JsonValue.String("Default")),
            ("restartRequired", JsonValue.Bool(false)), ("applied", JsonValue.Bool(true))),
            ControlTools.RestoredText("Work"), false), result);
        Assert.Equal(store, rig.H.StoreOnDisk());
        Assert.Equal(claude, rig.H.ClaudeServers());
        // Nothing saved.
        Assert.Equal(files, BackupFiles(rig));
    }

    /// <summary>The collection Claude runs, already as the backup holds it, with a connector on: allowed, Claude's
    /// config is written again and the answer says to restart, as a restore that changed it would. The master list
    /// and its backups stay as they were.</summary>
    [Fact]
    public void AnUnchangedActiveCollectionHasClaudesConfigWrittenAgain()
    {
        using var rig = Rig();
        var held = rig.State.Store.Collections["Default"].Clone();
        Assert.Contains(held.Mcps.Values, entry => entry.Enabled);
        var id = Backup(rig, rig.State.Store);
        var store = rig.H.StoreOnDisk();
        var files = CollectionBackupFiles(rig);
        rig.H.ClaudeRunningSince(hours: 1);
        rig.H.Settings.LastApplyDate = null;
        rig.State.RefreshRestartState();
        Assert.False(rig.State.NeedsClaudeRestart);
        var result = rig.Call("restore_collection", Arguments(id, "Default"));
        Assert.NotNull(result);
        Assert.Equal([CollectionRestoreModel.UnchangedLine(held.Mcps.Count)], rig.Confirmer.Requests[^1].Code);
        Assert.Equal(ControlTools.TakesEffectFooter, rig.Confirmer.Requests[^1].Footer);
        Assert.Equal(ControlTools.RestoredText("Default") + " " + ControlTools.RestartToApplyText, result.Text);
        Assert.Equal(JsonValue.Bool(true), result.Structured.ValueAt(new JsonPointer(["restartRequired"])));
        // Claude's config was written again.
        Assert.NotNull(rig.H.Settings.LastApplyDate);
        Assert.Equal(store, rig.H.StoreOnDisk());
        Assert.Equal(held, rig.State.Store.Collections["Default"]);
        // Nothing saved.
        Assert.Equal(files, CollectionBackupFiles(rig));
    }

    /// <summary>The collection Claude runs, already as the backup holds it, with no connector on: allowed, a write of
    /// Claude's config that failed earlier is tried again, as a restore that changed the collection would try it, and
    /// the answer says it reached Claude.</summary>
    [Fact]
    public void AnUnchangedActiveCollectionTriesAFailedWriteAgain()
    {
        using var rig = Rig();
        using var block = new WriteBlock(rig.H.ClaudeConfigPath);
        Assert.True(block.IsEffective);
        foreach (var name in rig.State.Store.Collections["Default"].Mcps.Where(p => p.Value.Enabled).Select(p => p.Key).ToList())
        {
            rig.State.SetEnabled(name, false);
        }
        Assert.True(rig.State.ApplyRetryNeeded);
        block.Dispose();
        var held = rig.State.Store.Collections["Default"].Clone();
        var id = Backup(rig, rig.State.Store);
        var result = rig.Call("restore_collection", Arguments(id, "Default"));
        Assert.NotNull(result);
        Assert.Equal([CollectionRestoreModel.UnchangedLine(held.Mcps.Count)], rig.Confirmer.Requests[^1].Code);
        Assert.Equal(ControlTools.RestoredText("Default"), result.Text);
        Assert.Equal(JsonValue.Bool(true), result.Structured.ValueAt(new JsonPointer(["applied"])));
        Assert.False(rig.State.ApplyRetryNeeded);
        // Claude's config was written.
        Assert.Equal([ClaudeIntegration.EntryName], rig.H.ClaudeServers().Keys);
        Assert.Equal(held, rig.State.Store.Collections["Default"]);
    }

    /// <summary>Claude can't see a value, so a right guess at one leaves the collection as the backup holds it and a
    /// wrong one doesn't: both restores get the same answer, restart and all, whether the connector holding the value
    /// is on or off, and whether the collection is the one Claude runs.</summary>
    [Theory]
    [InlineData("Default", false)]
    [InlineData("Default", true)]
    [InlineData("Work", true)]
    public void AnUnchangedRestoreIsAnsweredAsOneThatChangesSomething(string collection, bool on)
    {
        using var rig = Rig();
        if (collection != "Default")
        {
            Assert.Null(rig.State.AddEmptyCollection(collection));
        }
        Assert.Contains(rig.State.Store.Collections["Default"].Mcps.Values, entry => entry.Enabled);
        Assert.Null(rig.State.Upsert("vault", Vault(Secret, on), null, collection));
        rig.State.ApplyInteractively();
        var id = Backup(rig, rig.State.Store);
        rig.H.ClaudeRunningSince(hours: 1);
        rig.State.RefreshRestartState();
        var right = rig.Call("restore_collection", Arguments(id, collection));
        Assert.NotNull(right);
        Assert.Null(rig.State.Upsert("vault", Vault("guess", on), "vault", collection));
        rig.State.ApplyInteractively();
        var wrong = rig.Call("restore_collection", Arguments(id, collection));
        Assert.Equal(Vault(Secret, on), rig.State.Store.Collections[collection].Mcps["vault"]);
        // The same answer.
        Assert.Equal(right, wrong);
        Assert.Equal(collection == "Default" ? ControlTools.RestoredText(collection) + " " + ControlTools.RestartToApplyText
                                             : ControlTools.RestoredText(collection), right.Text);
    }

    /// <summary>The guess end to end: Claude sets a variable to the value it already holds, which is asked and answered
    /// as a change, then restores the collection from the newest backup, which holds it as it is. The restore is asked
    /// about, not refused, so no refusal tells Claude it guessed right.</summary>
    [Fact]
    public void AGuessThenARestoreIsAskedNotRefused()
    {
        using var rig = Rig();
        Assert.Null(rig.State.Upsert("vault", Vault(Secret), null));
        rig.State.ApplyInteractively();
        rig.H.ClaudeRunningSince(hours: 1);
        var set = rig.Call("set_connector_env", JsonValue.Object(("name", JsonValue.String("vault")),
            ("set", JsonValue.Object(("API_KEY", JsonValue.String(Secret))))));
        Assert.False(set?.IsError);
        var newest = Path.GetFileName(Backups(rig)[0]);
        var count = rig.State.Store.Collections["Default"].Mcps.Count;
        var result = rig.Call("restore_collection", Arguments(newest, "Default"));
        Assert.NotNull(result);
        Assert.Equal(2, rig.Confirmer.Requests.Count);
        Assert.Equal([CollectionRestoreModel.UnchangedLine(count)], rig.Confirmer.Requests[^1].Code);
        Assert.Equal(ControlTools.RestoredText("Default") + " " + ControlTools.RestartToApplyText, result.Text);
        Assert.Equal(Vault(Secret), rig.State.Store.Collections["Default"].Mcps["vault"]);
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

    /// <summary>A backup rewritten while the question was up, where the question reads the same, is not restored
    /// from: the Allow lands nothing.</summary>
    [Fact]
    public void ABackupThatChangedWhileAskingIsNotRestoredFrom()
    {
        using var rig = Rig(answer: null);
        Assert.Null(rig.State.AddEmptyCollection("Work"));
        Assert.Null(rig.State.Upsert("one", new McpEntry(Config("a")), null, "Work"));
        var id = Backup(rig, rig.State.Store);
        Assert.Null(rig.State.Upsert("one", new McpEntry(Config("b")), "one", "Work"));
        var rewritten = MasterStoreIO.Read(Path.Combine(rig.H.BackupsDir, id));
        Assert.NotNull(rewritten);
        rewritten.Collections["Work"].Mcps["one"] = new McpEntry(Config("z"));
        var answer = rig.Start("restore_collection", Arguments(id, "Work"));
        Backup(rig, rewritten, id);
        rig.Confirmer.Answer(true);
        rig.H.Drain();
        Assert.Equal(ControlResult.Refusal(ControlTools.ChangedWhileAskingError), answer.Result);
        // Nothing restored.
        Assert.Equal(Config("b"), rig.State.Store.Collections["Work"].Mcps["one"].Config);
        rig.Start("restore_collection", Arguments(id, "Work"));
        Assert.Equal(2, rig.Confirmer.Requests.Count);
        // The question itself reads the same.
        Assert.Equal(rig.Confirmer.Requests[0], rig.Confirmer.Requests[^1]);
    }

    /// <summary>With confirmations off nobody is asked, so the restore is announced, naming who asked; restored again,
    /// with nothing to change, it is announced the same way.</summary>
    [Fact]
    public void ARestoreNobodyWasAskedAboutIsAnnounced()
    {
        using var rig = new ControlRig(confirm: false);
        var (id, personal) = DeletedPersonal(rig);
        Assert.Equal(ControlTools.RestoredText("Personal"), rig.Call("restore_collection", Arguments(id, "Personal"))?.Text);
        Assert.Empty(rig.Confirmer.Requests);
        Assert.Equal([ControlTools.RestoredNotification(ControlCaller.ClaudeName, "Personal")], rig.H.Notifier.Sent.Select(sent => sent.Body));
        Assert.Equal(personal, rig.State.Store.Collections["Personal"]);
        Assert.Equal(ControlTools.RestoredText("Personal"), rig.Call("restore_collection", Arguments(id, "Personal"))?.Text);
        Assert.Equal(Enumerable.Repeat(ControlTools.RestoredNotification(ControlCaller.ClaudeName, "Personal"), 2),
            rig.H.Notifier.Sent.Select(sent => sent.Body));
        Assert.Equal(personal, rig.State.Store.Collections["Personal"]);
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
        // Before the call: the restore's own backup is listed after it, and the question never named it.
        var workLabel = Label(rig, id);
        var work = rig.Call("restore_collection", Arguments(id, "Work"));
        Assert.NotNull(work);
        Assert.Equal(["− extra"], rig.Confirmer.Requests[^1].Code);
        Assert.Null(rig.Confirmer.Requests[^1].Footer);
        Assert.Equal(ControlTools.RestoreCollectionText("Work", workLabel), rig.Confirmer.Requests[^1].Message);
        Assert.Equal(ControlTools.RestoredText("Work"), work.Text);
        Assert.Equal(JsonValue.Bool(false), work.Structured.ValueAt(new JsonPointer(["restartRequired"])));
        var active = rig.Call("restore_collection", Arguments(id, "Default"));
        Assert.NotNull(active);
        Assert.Equal(ControlTools.TakesEffectFooter, rig.Confirmer.Requests[^1].Footer);
        Assert.Equal(ControlTools.RestoredText("Default") + " " + ControlTools.RestartToApplyText, active.Text);
        Assert.Equal(JsonValue.Bool(true), active.Structured.ValueAt(new JsonPointer(["restartRequired"])));
        Assert.False(rig.H.ClaudeServers().ContainsKey("extra"));
    }

    /// <summary>Two backups of one second: the question names the one asked about with its milliseconds, so its
    /// label is worked out over every backup listed, as <c>list_backups</c> and the dialog list them.</summary>
    [Fact]
    public void TheQuestionLabelsTheBackupAmongAllListedOnes()
    {
        using var rig = Rig();
        Assert.Null(rig.State.AddEmptyCollection("Work"));
        var first = Backup(rig, rig.State.Store, "mcps.2026-09-04T12-00-00-000Z.json");
        Backup(rig, rig.State.Store, "mcps.2026-09-04T12-00-00-500Z.json");
        Assert.Null(rig.State.Upsert("extra", new McpEntry(Config("x")), null, "Work"));
        var listed = Label(rig, first);
        Assert.EndsWith(".000", listed, StringComparison.Ordinal);
        Assert.NotEqual(listed, BackupManager.Labels(Backups(rig).Where(path => Path.GetFileName(path) == first).ToList())[first]);
        Assert.NotNull(rig.Call("restore_collection", Arguments(first, "Work")));
        Assert.Equal(ControlTools.RestoreCollectionText("Work", listed), rig.Confirmer.Requests[^1].Message);
    }

    /// <summary>A deleted collection and one of its connectors, each named with a quote and a newline: the
    /// question's title, message and code lines show both as escapes, the way every control text does.</summary>
    [Fact]
    public void TheQuestionEscapesTheNamesItShows()
    {
        using var rig = Rig();
        const string odd = "Odd\"\nname";
        const string connector = "con\"\nnector";
        Assert.Null(rig.State.AddEmptyCollection(odd));
        Assert.Null(rig.State.Upsert(connector, new McpEntry(Config("x")), null, odd));
        var id = Backup(rig, rig.State.Store);
        Assert.Null(rig.State.DeleteCollection(odd));
        var time = Label(rig, id);
        Assert.NotNull(rig.Call("restore_collection", Arguments(id, odd)));
        var shown = ControlText.EscapedName(odd);
        Assert.DoesNotContain("\n", shown, StringComparison.Ordinal);
        Assert.Equal([new ConfirmationRequest(
            ControlTools.RestoreTitle("Claude", shown),
            ControlTools.RestoreCollectionCreatedText(shown, time),
            [$"+ {ControlText.EscapedName(connector)} (on)"], null)], rig.Confirmer.Requests);
    }
}

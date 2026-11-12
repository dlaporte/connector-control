using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// The shared recordings, reproduced exactly. The Mac writes them with
/// <c>scripts/regenerate-goldens.sh</c>; a difference here is a difference between the platforms in
/// what the tools say, to be fixed in the code that is wrong, never by editing a recording.
///
/// Mirror: Tests/ConnectorControlStateTests/ControlRecordingTests.swift
/// </summary>
public class ControlRecordingTests
{
    private static ControlTranscript.Replay Check(string name)
    {
        var replay = ControlTranscript.Run(name);
        try
        {
            Assert.True(replay.Expected.Count > 0, $"{name} has no recorded answers");
            Assert.Equal(replay.Expected, replay.Actual);
            // A recording holds nothing of the machine that made it: the harness's temp folder is
            // named uniquely each run, so a line that carried it would never match again.
            var temp = Path.GetFileName(replay.Rig.H.Dir.Path);
            Assert.DoesNotContain(replay.Actual, line => line.Contains(temp, StringComparison.Ordinal));
        }
        catch
        {
            // An xUnit assertion throws, where XCTest's carries on to the test's own dispose.
            replay.Rig.Dispose();
            throw;
        }
        return replay;
    }

    [Fact]
    public void Handshake() => Check("handshake").Rig.Dispose();

    [Fact]
    public void Reads()
    {
        using var rig = Check("reads").Rig;
        Assert.False(rig.State.Store.Mcps.ContainsKey(ClaudeIntegration.EntryName), "the seeded entry was never ingested");
        Assert.Single(rig.State.Service.Backups.Backups(RestoreModel.Series));
    }

    [Fact]
    public void Masking()
    {
        using var rig = Check("masking").Rig;
        var recording = Fixtures.Text("control/masking.transcript");
        foreach (var secret in new[] { "ghp_0123456789abcdefghijklmnopqrstuvwxyzAB", "s3cr3t-t0ken", "tenant=acme" })
        {
            Assert.DoesNotContain(secret, recording, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DirectWrite()
    {
        using var rig = Check("direct-write").Rig;
        Assert.False(rig.State.Store.Mcps["github"].Enabled);
        Assert.True(rig.H.Settings.ClaudeIntegrationEnabled, "Claude cannot turn the integration off");
        Assert.True(rig.H.Settings.ConfirmClaudeChanges, "Claude cannot turn confirmations off");
    }

    [Fact]
    public void GatedAllowed()
    {
        using var rig = Check("gated-allowed").Rig;
        Assert.Equal([ControlTools.AddTitle(ControlCaller.ClaudeName, "notion")], rig.Confirmer.Requests.Select(request => request.Title));
        Assert.True(rig.State.Store.Mcps.ContainsKey("notion"));
        Assert.Empty(rig.H.Notifier.Sent);
    }

    [Fact]
    public void GatedDeclined()
    {
        using var rig = Check("gated-declined").Rig;
        Assert.Single(rig.Confirmer.Requests);
        Assert.False(rig.State.Store.Mcps.ContainsKey("notion"));
    }

    /// <summary>Claude Desktop's agent mode introduces itself as <c>local-agent-mode-&lt;key&gt;</c>, and is Claude too.</summary>
    [Fact]
    public void GateOff()
    {
        using var rig = Check("gate-off").Rig;
        Assert.Empty(rig.Confirmer.Requests);
        Assert.Equal([ControlTools.AddedNotification(ControlCaller.ClaudeName, "notion", "Default")], rig.H.Notifier.Sent.Select(sent => sent.Body));
    }

    /// <summary>One variable added beside a secret: the question shows that variable alone, the answer no value,
    /// and the secret stays as it was. Sending back the masked config is refused before anyone is asked.</summary>
    [Fact]
    public void SetEnv()
    {
        using var rig = Check("set-env").Rig;
        Assert.Equal([ControlTools.ChangeTitle(ControlCaller.ClaudeName, "github")], rig.Confirmer.Requests.Select(request => request.Title));
        Assert.Equal(["env", "  + GITHUB_TOOLSETS = repos,issues"], Assert.Single(rig.Confirmer.Requests).Code);
        Assert.Equal(JsonValue.Object(("GITHUB_PERSONAL_ACCESS_TOKEN", JsonValue.String("ghp_0123456789abcdefghijklmnopqrstuvwxyzAB")),
            ("GITHUB_TOOLSETS", JsonValue.String("repos,issues"))), rig.State.Store.Mcps["github"].Config["env"]);
        Assert.DoesNotContain("ghp_0123456789abcdefghijklmnopqrstuvwxyzAB", Fixtures.Text("control/set-env.transcript"), StringComparison.Ordinal);
    }

    /// <summary>A collection the master list no longer holds, listed with the backups and restored: the question
    /// names the backup by its label (local time, so not written out) and lists every connector that comes back
    /// with its switch, and the collection is back as the backup holds it.</summary>
    [Fact]
    public void RestoreCollection()
    {
        using var rig = Check("restore-collection").Rig;
        const string id = "mcps.2026-09-04T12-00-00-000Z.json";
        // The one backup the replay seeded, which is all the question listed: the restore's own backup
        // is listed by now, and the question never named it.
        var listed = rig.State.Service.Backups.Backups(CollectionRestoreModel.Series).Where(path => Path.GetFileName(path) == id).ToList();
        var time = BackupManager.Labels(listed)[id];
        Assert.Equal([new ConfirmationRequest(
            ControlTools.RestoreTitle(ControlCaller.ClaudeName, "Personal"),
            ControlTools.RestoreCollectionCreatedText("Personal", time),
            ["+ aws-mcp (on)", "+ github (off)"], null)], rig.Confirmer.Requests);
        var backup = MasterStoreIO.Read(Fixtures.Path("control/collection_backup.json"));
        Assert.NotNull(backup);
        Assert.Equal(backup.Collections["Personal"], rig.State.Store.Collections["Personal"]);
        Assert.Equal("Default", rig.State.ActiveCollection);
    }

    [Fact]
    public void IntegrationOff() => Check("integration-off").Rig.Dispose();

    [Fact]
    public void ReservedName()
    {
        using var rig = Check("reserved-name").Rig;
        Assert.Empty(rig.Confirmer.Requests);
        Assert.False(rig.State.Store.Mcps.ContainsKey(ClaudeIntegration.EntryName));
    }

    /// <summary>The command-line tool is the user: never asked, never announced, and the only caller that may
    /// change the two Claude settings.</summary>
    [Fact]
    public void CommandLine()
    {
        using var rig = Check("cli").Rig;
        Assert.Empty(rig.Confirmer.Requests);
        Assert.True(rig.State.Store.Mcps.ContainsKey("notion"));
        Assert.False(rig.H.Settings.ConfirmClaudeChanges);
        Assert.False(rig.H.Settings.ClaudeIntegrationEnabled);
        Assert.Empty(rig.H.Notifier.Sent);
    }

    /// <summary>A relay that gives the command-line tool's own name is still the relay.</summary>
    [Fact]
    public void Impersonation()
    {
        using var rig = Check("impersonation").Rig;
        Assert.Equal([ControlTools.AddTitle(ControlCaller.CallingItselfText("connector-control"), "notion")], rig.Confirmer.Requests.Select(request => request.Title));
        Assert.False(rig.State.Store.Mcps.ContainsKey("notion"));
        Assert.True(rig.H.Settings.ClaudeIntegrationEnabled);
    }

    [Fact]
    public void ProtocolErrors()
    {
        var replay = Check("protocol-errors");
        using var rig = replay.Rig;
        Assert.Equal("claude-ai", replay.Session.ClientName);
    }

    /// <summary>A directive the replay does not know fails it, rather than leaving the rig at its default.</summary>
    [Fact]
    public void ADirectiveTheReplayDoesNotKnowFailsIt()
    {
        foreach (var line in new[] { "#! confirm of", "#! caller CLI", "#! colour on", "#! backup off", "#! backup", "#! answer  allow",
                                     "#! answer allow ", "#!caller cli", "caller cli", "#! collection-backup off" })
        {
            Assert.Throws<FormatException>(() => ControlTranscript.Directives([line]));
        }
        Assert.Throws<FormatException>(() => ControlTranscript.Directives(["#! confirm on", "#! confirm off"]));
        Assert.Equal(new Dictionary<string, string> { ["caller"] = "cli", ["answer"] = "deny" },
            ControlTranscript.Directives(["# A comment.", "#! caller cli", "#! answer deny", "> {}", "< {}"]));
        Assert.Equal(new Dictionary<string, string> { ["backup"] = "on", ["collection-backup"] = "on" },
            ControlTranscript.Directives(["#! backup on", "#! collection-backup on"]));
    }
}

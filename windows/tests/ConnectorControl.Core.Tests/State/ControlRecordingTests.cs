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
                                     "#! answer allow ", "#!caller cli", "caller cli" })
        {
            Assert.Throws<FormatException>(() => ControlTranscript.Directives([line]));
        }
        Assert.Throws<FormatException>(() => ControlTranscript.Directives(["#! confirm on", "#! confirm off"]));
        Assert.Equal(new Dictionary<string, string> { ["caller"] = "cli", ["answer"] = "deny" },
            ControlTranscript.Directives(["# A comment.", "#! caller cli", "#! answer deny", "> {}", "< {}"]));
    }
}

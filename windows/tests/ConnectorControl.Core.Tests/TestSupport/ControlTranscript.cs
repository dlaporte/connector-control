using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>
/// Replays one shared recording under <c>Tests/Fixtures/control/</c> against a fresh rig seeded from
/// <c>claude_config.json</c>, through a real <see cref="McpSession"/> and <see cref="ControlToolHost"/>.
/// The Mac writes the recordings; this side only compares, byte for byte.
///
/// A recording is UTF-8 with <c>\n</c> line ends. <c># </c> lines are comments; <c>#! &lt;key&gt; &lt;value&gt;</c>
/// lines set up the rig (<c>integration on|off</c>, <c>confirm on|off</c>, <c>answer allow|deny</c>,
/// <c>caller relay|cli</c>, <c>backup on</c>); <c>&gt; </c> lines go to the session in order, and each line
/// the session sends back is a <c>&lt; </c> line after the request that drew it. The master list's path,
/// the one temp path a result carries, is <c>&lt;master-list&gt;</c> in a recording.
///
/// Mirror: Tests/ConnectorControlStateTests/TestSupport/ControlTranscript.swift
/// </summary>
public static class ControlTranscript
{
    public const string MasterListToken = "<master-list>";
    /// <summary>Each directive's key and the values it takes.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> DirectiveValues = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["integration"] = ["on", "off"], ["confirm"] = ["on", "off"], ["answer"] = ["allow", "deny"],
        ["caller"] = ["relay", "cli"], ["backup"] = ["on"],
    };

    public sealed record Replay(ControlRig Rig, McpSession Session, IReadOnlyList<string> Expected, IReadOnlyList<string> Actual);

    /// <summary>The Mac names this <c>replay</c>: C# cannot name a method after a nested type of its class.</summary>
    public static Replay Run(string name)
    {
        var lines = Fixtures.Text($"control/{name}.transcript").Split('\n').Where(line => line.Length > 0).ToList();
        var settings = Directives(lines);
        var rig = new ControlRig(integration: settings.GetValueOrDefault("integration") != "off",
                                 confirm: settings.GetValueOrDefault("confirm") != "off",
                                 claudeConfig: Fixtures.Text("control/claude_config.json"));
        rig.Confirmer.Scripted = settings.GetValueOrDefault("answer") switch
        {
            "allow" => true,
            "deny" => false,
            _ => null,
        };
        if (settings.GetValueOrDefault("backup") == "on")
        {
            rig.State.Service.Backups.BackUp(rig.H.ClaudeConfigPath, RestoreModel.Series, rig.H.Now);
        }
        var host = new ControlToolHost(rig.Tools, isCli: settings.GetValueOrDefault("caller") == "cli");
        var sent = new List<string>();
        var session = new McpSession(host, host.Version, sent.Add);
        host.Session = session;
        // The path as a JSON string spells it, without its quotes: how it appears inside any line.
        var escaped = JsonValue.String(rig.State.Service.Paths.MasterStorePath).CompactLine()[1..^1];
        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("< ", StringComparison.Ordinal))
            {
                expected.Add(line[2..]);
                continue;
            }
            if (!line.StartsWith("> ", StringComparison.Ordinal)) { continue; }
            sent.Clear();
            session.Receive(line[2..]);
            rig.H.Drain();
            // Ordinal, as the Mac's literal replacement is.
            actual.AddRange(sent.Select(produced => produced.Replace(escaped, MasterListToken, StringComparison.Ordinal)));
        }
        return new Replay(rig, session, expected, actual);
    }

    /// <summary>The rig's set-up from a recording's <c>#!</c> lines. A key or value it does not know, a key
    /// given twice, or a line of none of the four kinds fails the replay, rather than leaving the rig at
    /// its default unnoticed. The Mac throws its own <c>FormatError</c>.</summary>
    public static Dictionary<string, string> Directives(IEnumerable<string> lines)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.StartsWith("#!", StringComparison.Ordinal))
            {
                var parts = line.StartsWith("#! ", StringComparison.Ordinal) ? line[3..].Split(' ') : [];
                if (parts.Length != 2 || !DirectiveValues.TryGetValue(parts[0], out var values) || !values.Contains(parts[1], StringComparer.Ordinal))
                {
                    throw new FormatException($"Not a directive the replay knows: {line}");
                }
                if (!settings.TryAdd(parts[0], parts[1])) { throw new FormatException($"A directive given twice: {line}"); }
            }
            else if (!new[] { "# ", "> ", "< " }.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal)))
            {
                throw new FormatException($"Not a line the replay knows: {line}");
            }
        }
        return settings;
    }
}

using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// Mirror: Tests/ConnectorControlStateTests/AppStateUpgradeTests.swift
///
/// An install upgraded from 1.3, which knew collections as profiles: Tests/Fixtures/v1.3/ holds the
/// master list 1.3 wrote (schema 2, two profiles, "Work" active, some connectors off) and the
/// Claude config it left applied. There is no collections.json and no collections-local.json: 1.3
/// never wrote either.
/// </summary>
public class AppStateUpgradeTests
{
    /// <summary>Each profile's connectors and whether they are on, as 1.3 wrote them.</summary>
    private static readonly Dictionary<string, Dictionary<string, bool>> Flags13 = new(StringComparer.Ordinal)
    {
        ["Default"] = new(StringComparer.Ordinal) { ["aws-mcp"] = true, ["scoutbook"] = false, ["service-now"] = true },
        ["Work"] = new(StringComparer.Ordinal) { ["jira"] = true, ["ledger"] = false, ["scoutbook"] = true },
    };

    /// <summary>Places the 1.3 install's two files where this app looks for them.</summary>
    private static (byte[] Store, byte[] Claude) InstallVersion13(AppStateHarness h)
    {
        var store = Fixtures.Bytes(Path.Combine("v1.3", "mcps.json"));
        var claude = Fixtures.Bytes(Path.Combine("v1.3", "claude_desktop_config.json"));
        Directory.CreateDirectory(h.StoreDir);
        File.WriteAllBytes(h.MasterStorePath, store);
        File.WriteAllBytes(h.ClaudeConfigPath, claude);
        return (store, claude);
    }

    /// <summary>The keys 1.3's decoder requires at every level above a connector's own config: the
    /// file's root, each profile, and each connector entry. 1.3 fails to decode a file that lacks
    /// one.</summary>
    private static List<string> Shape(byte[] data)
    {
        static string Keys(JsonValue value, string path) => $"{path}: {string.Join(",", value.ObjectProperties.Keys)}";
        var root = JsonValue.Parse(data);
        var shape = new List<string> { Keys(root, "") };
        foreach (var (name, profile) in root.ObjectProperties["profiles"].ObjectProperties)
        {
            shape.Add(Keys(profile, $"/profiles/{name}"));
            foreach (var (connector, entry) in profile.ObjectProperties["mcps"].ObjectProperties)
            {
                shape.Add(Keys(entry, $"/profiles/{name}/mcps/{connector}"));
            }
        }
        return shape;
    }

    [Fact]
    public void AVersion13InstallOpensItsProfilesAsLocalCollectionsAndStaysReadableBy13()
    {
        using var h = new AppStateHarness(seedClaudeConfig: false);
        var written = InstallVersion13(h);
        Assert.False(File.Exists(Path.Combine(h.StoreDir, CollectionsFile.FileName)));
        Assert.False(File.Exists(Path.Combine(h.StoreDir, CollectionsLocalCache.FileName)));

        using var state = h.Create();

        // Every profile is a local collection under its own name, and the active one stays active.
        Assert.Equal(["Default", "Work"], state.CollectionNames);
        foreach (var name in state.CollectionNames)
        {
            Assert.Equal(CollectionKind.Local, state.KindOf(name));
            Assert.False(state.IsPublished(name), name);
        }
        Assert.Equal("Work", state.ActiveCollection);
        Assert.Null(state.CollectionBanner);
        Assert.Null(state.LastError);

        // Connectors, their configs and their on/off flags are exactly what 1.3 wrote.
        Assert.Equal(MasterStoreIO.Read(Fixtures.Path(Path.Combine("v1.3", "mcps.json"))), state.Store);
        Assert.Equal(Flags13, state.Store.Collections.ToDictionary(
            c => c.Key, c => c.Value.Mcps.ToDictionary(m => m.Key, m => m.Value.Enabled, StringComparer.Ordinal), StringComparer.Ordinal));

        // The upgrade alone gives Claude nothing new to run, so its config is left as it was.
        Assert.Equal(written.Claude, File.ReadAllBytes(h.ClaudeConfigPath));
        Assert.Null(h.Settings.LastApplyDate);   // nothing was applied
        Assert.False(state.NeedsClaudeRestart);

        // The master list keeps 1.3's keys, whether or not the launch rewrote it...
        var shape13 = Shape(written.Store);
        Assert.Equal(shape13, Shape(File.ReadAllBytes(h.MasterStorePath)));
        Assert.Equal(state.Store, h.StoreOnDisk());

        // ...and after a save from this version, which a 1.3 install on another machine or after a
        // downgrade still has to read: the same keys, schema version 2, the same content.
        state.SetEnabled("ledger", true, "Work");
        state.SetEnabled("ledger", false, "Work");
        var saved = File.ReadAllBytes(h.MasterStorePath);
        Assert.Equal(shape13, Shape(saved));
        Assert.Equal(JsonValue.Int(2), JsonValue.Parse(saved).ObjectProperties["version"]);
        Assert.Equal(JsonValue.Parse(written.Store), JsonValue.Parse(saved));

        // With nothing published or subscribed, this launch left the sidecar valid and empty.
        // The next launch reads it as empty, not as unreadable, so the first publish still lands.
        var sidecar = Path.Combine(h.StoreDir, CollectionsFile.FileName);
        Assert.Equal(new CollectionsFile([]), CollectionsFile.Decode(JsonValue.Parse(File.ReadAllBytes(sidecar))));
        state.Dispose();
        using var second = h.Create();
        h.Publish(second, "Work");
        Assert.Null(second.LastError);
        Assert.NotNull(CollectionsFile.Load(sidecar).Collections["Work"].Publish);
    }
}

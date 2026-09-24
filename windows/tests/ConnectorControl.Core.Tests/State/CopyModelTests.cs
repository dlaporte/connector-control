using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// Mirror: Tests/ConnectorControlStateTests/CopyModelTests.swift.
/// The copy clash sheet: what its rows say about the ticked connectors a destination already holds,
/// and what Perform() does with the answers.
/// </summary>
public class CopyModelTests
{
    private static McpEntry Local(string command, params string[] args) =>
        new(JsonValue.Object(
            ("command", JsonValue.String(command)),
            ("args", JsonValue.Array(args.Select(JsonValue.String)))));

    /// <summary>
    /// Rows mirror the ticks in their display order, and Clashes follows CheckedNamesClashing
    /// exactly: a clash defaults to KeepBoth and carries no badge, a clean arrival carries
    /// ImportModel.NewBadge and no picker to default.
    /// </summary>
    [Fact]
    public void RowsMirrorTheTicksWithClashesDefaultedToKeepBoth()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        // Created before any connector is seeded: CreateCollection copies whichever collection
        // is active when it is made, so seeding "Spare" first would carry today's connectors
        // along with it and change what clashes below.
        Assert.Null(state.AddEmptyCollection("Spare"));
        foreach (var name in new[] { "zeta", "alpha", "beta" })
        {
            Assert.Null(state.Upsert(name, Local("/bin/" + name), null, "Default"));
        }
        foreach (var name in new[] { "zeta", "alpha" })
        {
            Assert.Null(state.Upsert(name, Local("/bin/other"), null, "Spare"));
        }
        using var collections = new CollectionsModel(state, h.Dialogs);
        collections.Selected = "Default";
        collections.SetChecked("zeta", true);
        collections.SetChecked("beta", true);
        collections.SetChecked("alpha", true);

        var model = new CopyModel(collections, "Spare");
        Assert.Equal("Spare", model.Destination);
        // CheckedNames follows the rows, which the window shows in ordinal order — not the order
        // the ticks were made in.
        Assert.Equal(["alpha", "beta", "zeta"], model.Rows.Select(r => r.Name));   // the ticks' own display order
        Assert.Equal(["alpha", "beta", "zeta"], model.Rows.Select(r => r.Id));
        Assert.Equal([true, false, true], model.Rows.Select(r => r.Clashes));
        Assert.Equal([ImportChoice.KeepBoth, ImportChoice.KeepBoth, ImportChoice.KeepBoth],
            model.Rows.Select(r => r.Choice));   // keepBoth by default, clash or not
        Assert.Equal(["", ImportModel.NewBadge, ""], model.Rows.Select(r => r.Badge));
        Assert.Equal([true, false, true], model.Rows.Select(r => r.ShowsPicker));   // every clash asks
    }

    [Fact]
    public void ARowThatDoesNotClashIsBadgedNew()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.AddEmptyCollection("Spare"));
        Assert.Null(state.Upsert("alpha", Local("/bin/alpha"), null, "Default"));
        using var collections = new CollectionsModel(state, h.Dialogs);
        collections.Selected = "Default";
        collections.SetChecked("alpha", true);

        var model = new CopyModel(collections, "Spare");
        Assert.Equal([ImportModel.NewBadge], model.Rows.Select(r => r.Badge));
    }

    /// <summary>Replace takes over the destination's own entry under its own name.</summary>
    [Fact]
    public void PerformWithReplaceReplacesTheDestinationsEntry()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.AddEmptyCollection("Spare"));
        Assert.Null(state.Upsert("alpha", Local("/bin/alpha", "new"), null, "Default"));
        Assert.Null(state.Upsert("alpha", Local("/bin/old"), null, "Spare"));
        using var collections = new CollectionsModel(state, h.Dialogs);
        collections.Selected = "Default";
        collections.SetChecked("alpha", true);

        var model = new CopyModel(collections, "Spare");
        model.Rows[0].Choice = ImportChoice.Replace;
        Assert.True(model.Perform());
        // The incoming copy replaced the one that was there.
        Assert.Equal(Local("/bin/alpha", "new").Config, state.Store.Collections["Spare"].Mcps["alpha"].Config);
        Assert.Empty(collections.CheckedNames);   // the ticks went with it
    }

    /// <summary>
    /// A Replace over a connector that is on in the active collection lands the copy off, so
    /// Claude must stop running the old one at once; into an inactive collection nothing Claude
    /// runs changes.
    /// </summary>
    [Fact]
    public void PerformWithReplaceAppliesOnlyWhenTheDestinationIsActive()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.AddEmptyCollection("Spare"));
        Assert.Null(state.AddEmptyCollection("Work"));
        Assert.Null(state.Upsert("scoutbook", Local("/bin/scoutbook"), null, "Spare"));
        Assert.Null(state.Upsert("scoutbook", Local("/bin/old"), null, "Work"));
        using var collections = new CollectionsModel(state, h.Dialogs);
        collections.Selected = "Spare";

        // An enabled connector in the active collection that has not been applied yet: an apply
        // from the inactive leg would write it, so that leg can catch an unconditional one.
        Assert.Null(state.Upsert("delta", Local("/bin/delta"), null, "Default"));
        Assert.False(h.ClaudeServers().ContainsKey("delta"));   // upserted, not applied

        // Inactive destination: the copy lands, but Claude's config is untouched.
        var before = h.ClaudeServers();
        collections.SetChecked("scoutbook", true);
        var inactive = new CopyModel(collections, "Work");
        inactive.Rows[0].Choice = ImportChoice.Replace;
        Assert.True(inactive.Perform());
        Assert.Equal(Local("/bin/scoutbook").Config, state.Store.Collections["Work"].Mcps["scoutbook"].Config);
        Assert.Equal(before, h.ClaudeServers());   // Work is not active, so nothing Claude runs has changed

        // Active destination: the enabled scoutbook Claude runs is replaced by a copy that is off.
        Assert.True(state.Store.Collections["Default"].Mcps["scoutbook"].Enabled);
        Assert.True(h.ClaudeServers().ContainsKey("scoutbook"));   // Claude runs it before the copy
        collections.SetChecked("scoutbook", true);
        var active = new CopyModel(collections, "Default");
        active.Rows[0].Choice = ImportChoice.Replace;
        Assert.True(active.Perform());
        Assert.False(state.Store.Collections["Default"].Mcps["scoutbook"].Enabled);
        Assert.False(h.ClaudeServers().ContainsKey("scoutbook"));   // the replaced connector is off, so Claude stops running it
    }

    /// <summary>
    /// Skip leaves the destination's own entry untouched, and a non-clashing ticked row still
    /// lands beside it.
    /// </summary>
    [Fact]
    public void PerformWithSkipLeavesTheDestinationsEntryUntouchedAndStillCopiesTheRest()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.AddEmptyCollection("Spare"));
        Assert.Null(state.Upsert("alpha", Local("/bin/alpha", "new"), null, "Default"));
        Assert.Null(state.Upsert("beta", Local("/bin/beta"), null, "Default"));
        Assert.Null(state.Upsert("alpha", Local("/bin/old"), null, "Spare"));
        using var collections = new CollectionsModel(state, h.Dialogs);
        collections.Selected = "Default";
        collections.SetChecked("alpha", true);
        collections.SetChecked("beta", true);

        var model = new CopyModel(collections, "Spare");
        model.Rows.Single(r => r.Name == "alpha").Choice = ImportChoice.Skip;
        Assert.True(model.Perform());
        // Untouched: still the entry that was already in Spare.
        Assert.Equal(Local("/bin/old").Config, state.Store.Collections["Spare"].Mcps["alpha"].Config);
        Assert.True(state.Store.Collections["Spare"].Mcps.ContainsKey("beta"));   // the non-clashing row still copied
    }

    /// <summary>The default the sheet opens with, left untouched: a clash lands beside the original.</summary>
    [Fact]
    public void PerformWithDefaultsLandsAClashBesideTheOriginal()
    {
        using var h = new AppStateHarness();
        using var state = h.Create();
        Assert.Null(state.AddEmptyCollection("Spare"));
        Assert.Null(state.Upsert("alpha", Local("/bin/alpha"), null, "Default"));
        Assert.Null(state.Upsert("alpha", Local("/bin/old"), null, "Spare"));
        using var collections = new CollectionsModel(state, h.Dialogs);
        collections.Selected = "Default";
        collections.SetChecked("alpha", true);

        var model = new CopyModel(collections, "Spare");
        Assert.True(model.Perform());
        Assert.True(state.Store.Collections["Spare"].Mcps.ContainsKey("alpha"));     // the original is untouched
        Assert.True(state.Store.Collections["Spare"].Mcps.ContainsKey("alpha 2"));   // the copy landed beside it
        Assert.Empty(collections.CheckedNames);   // Perform() clears the collections model's ticks on success
    }
}

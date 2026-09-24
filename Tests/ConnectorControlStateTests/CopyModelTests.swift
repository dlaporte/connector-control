import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/CopyModelTests.cs.
/// The copy clash sheet: what its rows say about the ticked connectors a destination already holds,
/// and what `perform()` does with the answers.
@MainActor
final class CopyModelTests: XCTestCase {

    /// Rows mirror the ticks in their display order, and `clashes` follows
    /// `checkedNamesClashing(in:)` exactly: a clash defaults to `.keepBoth` and carries no badge,
    /// a clean arrival carries `ImportModel.newBadge` and no picker to default.
    func testRowsMirrorTheTicksWithClashesDefaultedToKeepBoth() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        // Created before any connector is seeded: `createCollection` copies whichever collection
        // is active when it is made, so seeding "Spare" first would carry today's connectors
        // along with it and change what clashes below.
        XCTAssertNil(state.addEmptyCollection(named: "Spare"))
        for name in ["zeta", "alpha", "beta"] {
            XCTAssertNil(state.upsert(name: name, entry: AppStateHarness.localConnector("/bin/" + name), renamedFrom: nil, in: "Default"))
        }
        for name in ["zeta", "alpha"] {
            XCTAssertNil(state.upsert(name: name, entry: AppStateHarness.localConnector("/bin/other"), renamedFrom: nil, in: "Spare"))
        }
        let collections = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { collections.dispose() }
        collections.selected = "Default"
        collections.setChecked("zeta", true)
        collections.setChecked("beta", true)
        collections.setChecked("alpha", true)

        let model = CopyModel(collections: collections, destination: "Spare")
        XCTAssertEqual(model.destination, "Spare")
        // checkedNames follows the rows, which the window shows in ordinal order — not the order
        // the ticks were made in.
        XCTAssertEqual(model.rows.map(\.name), ["alpha", "beta", "zeta"], "the ticks' own display order")
        XCTAssertEqual(model.rows.map(\.id), ["alpha", "beta", "zeta"])
        XCTAssertEqual(model.rows.map(\.clashes), [true, false, true])
        XCTAssertEqual(model.rows.map(\.choice), [.keepBoth, .keepBoth, .keepBoth], "keepBoth by default, clash or not")
        XCTAssertEqual(model.rows.map(\.badge), ["", ImportModel.newBadge, ""])
        XCTAssertEqual(model.rows.map(\.showsPicker), [true, false, true], "every clash asks")
    }

    /// `.replace` takes over the destination's own entry under its own name.
    func testPerformWithReplaceReplacesTheDestinationsEntry() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.addEmptyCollection(named: "Spare"))
        XCTAssertNil(state.upsert(name: "alpha", entry: AppStateHarness.localConnector("/bin/alpha", ["new"]), renamedFrom: nil, in: "Default"))
        XCTAssertNil(state.upsert(name: "alpha", entry: AppStateHarness.localConnector("/bin/old"), renamedFrom: nil, in: "Spare"))
        let collections = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { collections.dispose() }
        collections.selected = "Default"
        collections.setChecked("alpha", true)

        let model = CopyModel(collections: collections, destination: "Spare")
        model.rows[0].choice = .replace
        XCTAssertTrue(model.perform())
        XCTAssertEqual(state.store.collections["Spare"]?.mcps["alpha"]?.config, AppStateHarness.localConnector("/bin/alpha", ["new"]).config,
                       "the incoming copy replaced the one that was there")
        XCTAssertEqual(collections.checkedNames, [], "the ticks went with it")
    }

    /// A Replace over a connector that is on in the active collection lands the copy off, so
    /// Claude must stop running the old one at once; into an inactive collection nothing Claude
    /// runs changes.
    func testPerformWithReplaceAppliesOnlyWhenTheDestinationIsActive() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.addEmptyCollection(named: "Spare"))
        XCTAssertNil(state.addEmptyCollection(named: "Work"))
        XCTAssertNil(state.upsert(name: "scoutbook", entry: AppStateHarness.localConnector("/bin/scoutbook"), renamedFrom: nil, in: "Spare"))
        XCTAssertNil(state.upsert(name: "scoutbook", entry: AppStateHarness.localConnector("/bin/old"), renamedFrom: nil, in: "Work"))
        let collections = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { collections.dispose() }
        collections.selected = "Spare"

        // An enabled connector in the active collection that has not been applied yet: an apply
        // from the inactive leg would write it, so that leg can catch an unconditional one.
        XCTAssertNil(state.upsert(name: "delta", entry: AppStateHarness.localConnector("/bin/delta"), renamedFrom: nil, in: "Default"))
        XCTAssertNil(try h.claudeServers()["delta"], "upserted, not applied")

        // Inactive destination: the copy lands, but Claude's config is untouched.
        let before = try h.claudeServers()
        collections.setChecked("scoutbook", true)
        let inactive = CopyModel(collections: collections, destination: "Work")
        inactive.rows[0].choice = .replace
        XCTAssertTrue(inactive.perform())
        XCTAssertEqual(state.store.collections["Work"]?.mcps["scoutbook"]?.config, AppStateHarness.localConnector("/bin/scoutbook").config)
        XCTAssertEqual(try h.claudeServers(), before, "Work is not active, so nothing Claude runs has changed")

        // Active destination: the enabled scoutbook Claude runs is replaced by a copy that is off.
        XCTAssertEqual(state.store.collections["Default"]?.mcps["scoutbook"]?.enabled, true)
        XCTAssertNotNil(try h.claudeServers()["scoutbook"], "Claude runs it before the copy")
        collections.setChecked("scoutbook", true)
        let active = CopyModel(collections: collections, destination: "Default")
        active.rows[0].choice = .replace
        XCTAssertTrue(active.perform())
        XCTAssertEqual(state.store.collections["Default"]?.mcps["scoutbook"]?.enabled, false)
        XCTAssertNil(try h.claudeServers()["scoutbook"], "the replaced connector is off, so Claude stops running it")
    }

    /// `.skip` leaves the destination's own entry untouched, and a non-clashing ticked row still
    /// lands beside it.
    func testPerformWithSkipLeavesTheDestinationsEntryUntouchedAndStillCopiesTheRest() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.addEmptyCollection(named: "Spare"))
        XCTAssertNil(state.upsert(name: "alpha", entry: AppStateHarness.localConnector("/bin/alpha", ["new"]), renamedFrom: nil, in: "Default"))
        XCTAssertNil(state.upsert(name: "beta", entry: AppStateHarness.localConnector("/bin/beta"), renamedFrom: nil, in: "Default"))
        XCTAssertNil(state.upsert(name: "alpha", entry: AppStateHarness.localConnector("/bin/old"), renamedFrom: nil, in: "Spare"))
        let collections = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { collections.dispose() }
        collections.selected = "Default"
        collections.setChecked("alpha", true)
        collections.setChecked("beta", true)

        let model = CopyModel(collections: collections, destination: "Spare")
        model.rows[model.rows.firstIndex { $0.name == "alpha" }!].choice = .skip
        XCTAssertTrue(model.perform())
        XCTAssertEqual(state.store.collections["Spare"]?.mcps["alpha"]?.config, AppStateHarness.localConnector("/bin/old").config,
                       "untouched: still the entry that was already in Spare")
        XCTAssertNotNil(state.store.collections["Spare"]?.mcps["beta"], "the non-clashing row still copied")
    }

    /// The default the sheet opens with, left untouched: a clash lands beside the original.
    func testPerformWithDefaultsLandsAClashBesideTheOriginal() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.addEmptyCollection(named: "Spare"))
        XCTAssertNil(state.upsert(name: "alpha", entry: AppStateHarness.localConnector("/bin/alpha"), renamedFrom: nil, in: "Default"))
        XCTAssertNil(state.upsert(name: "alpha", entry: AppStateHarness.localConnector("/bin/old"), renamedFrom: nil, in: "Spare"))
        let collections = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { collections.dispose() }
        collections.selected = "Default"
        collections.setChecked("alpha", true)

        let model = CopyModel(collections: collections, destination: "Spare")
        XCTAssertTrue(model.perform())
        XCTAssertNotNil(state.store.collections["Spare"]?.mcps["alpha"], "the original is untouched")
        XCTAssertNotNil(state.store.collections["Spare"]?.mcps["alpha 2"], "the copy landed beside it")
        XCTAssertEqual(collections.checkedNames, [], "perform() clears the collections model's ticks on success")
    }
}

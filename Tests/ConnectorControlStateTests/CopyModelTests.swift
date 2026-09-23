import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// windows/tests/ConnectorControl.Core.Tests/State/CopyModelTests.cs. The copy clash sheet: what
/// its rows say about the ticked connectors a destination already holds, and what `perform()`
/// does with the answers.
@MainActor
final class CopyModelTests: XCTestCase {
    private func local(_ command: String, _ args: [String] = []) -> MCPEntry {
        MCPEntry(config: .object(["command": .string(command), "args": .array(args.map(JSONValue.string))]))
    }

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
            XCTAssertNil(state.upsert(name: name, entry: local("/bin/" + name), renamedFrom: nil, in: "Default"))
        }
        for name in ["zeta", "alpha"] {
            XCTAssertNil(state.upsert(name: name, entry: local("/bin/other"), renamedFrom: nil, in: "Spare"))
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
        XCTAssertTrue(model.needsAnswers, "zeta and alpha clash")
    }

    func testNeedsAnswersIsFalseWhenNothingTickedClashes() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.addEmptyCollection(named: "Spare"))
        XCTAssertNil(state.upsert(name: "alpha", entry: local("/bin/alpha"), renamedFrom: nil, in: "Default"))
        let collections = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { collections.dispose() }
        collections.selected = "Default"
        collections.setChecked("alpha", true)

        let model = CopyModel(collections: collections, destination: "Spare")
        XCTAssertFalse(model.needsAnswers)
        XCTAssertEqual(model.rows.map(\.badge), [ImportModel.newBadge])
    }

    /// `.replace` takes over the destination's own entry under its own name.
    func testPerformWithReplaceReplacesTheDestinationsEntry() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.addEmptyCollection(named: "Spare"))
        XCTAssertNil(state.upsert(name: "alpha", entry: local("/bin/alpha", ["new"]), renamedFrom: nil, in: "Default"))
        XCTAssertNil(state.upsert(name: "alpha", entry: local("/bin/old"), renamedFrom: nil, in: "Spare"))
        let collections = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { collections.dispose() }
        collections.selected = "Default"
        collections.setChecked("alpha", true)

        let model = CopyModel(collections: collections, destination: "Spare")
        model.rows[0].choice = .replace
        XCTAssertTrue(model.perform())
        XCTAssertEqual(state.store.collections["Spare"]?.mcps["alpha"]?.config, local("/bin/alpha", ["new"]).config,
                       "the incoming copy replaced the one that was there")
        XCTAssertEqual(collections.checkedNames, [], "the ticks went with it")
    }

    /// `.skip` leaves the destination's own entry untouched, and a non-clashing ticked row still
    /// lands beside it.
    func testPerformWithSkipLeavesTheDestinationsEntryUntouchedAndStillCopiesTheRest() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.addEmptyCollection(named: "Spare"))
        XCTAssertNil(state.upsert(name: "alpha", entry: local("/bin/alpha", ["new"]), renamedFrom: nil, in: "Default"))
        XCTAssertNil(state.upsert(name: "beta", entry: local("/bin/beta"), renamedFrom: nil, in: "Default"))
        XCTAssertNil(state.upsert(name: "alpha", entry: local("/bin/old"), renamedFrom: nil, in: "Spare"))
        let collections = CollectionsModel(state: state, dialogs: h.dialogs)
        defer { collections.dispose() }
        collections.selected = "Default"
        collections.setChecked("alpha", true)
        collections.setChecked("beta", true)

        let model = CopyModel(collections: collections, destination: "Spare")
        model.rows[model.rows.firstIndex { $0.name == "alpha" }!].choice = .skip
        XCTAssertTrue(model.perform())
        XCTAssertEqual(state.store.collections["Spare"]?.mcps["alpha"]?.config, local("/bin/old").config,
                       "untouched: still the entry that was already in Spare")
        XCTAssertNotNil(state.store.collections["Spare"]?.mcps["beta"], "the non-clashing row still copied")
    }

    /// The default the sheet opens with, left untouched: a clash lands beside the original.
    func testPerformWithDefaultsLandsAClashBesideTheOriginal() throws {
        let (h, state) = AppStateHarness.started()
        defer { h.dispose() }
        XCTAssertNil(state.addEmptyCollection(named: "Spare"))
        XCTAssertNil(state.upsert(name: "alpha", entry: local("/bin/alpha"), renamedFrom: nil, in: "Default"))
        XCTAssertNil(state.upsert(name: "alpha", entry: local("/bin/old"), renamedFrom: nil, in: "Spare"))
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

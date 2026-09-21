import XCTest
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/CollectionDiffTests.cs
final class CollectionDiffTests: XCTestCase {
    private func rendered(_ connectors: [String: (JSONValue, [String: RenderedNeed])]) -> RenderedCollection {
        RenderedCollection(connectors: connectors.mapValues { RenderedConnector(config: $0.0, needs: $0.1, authoredOn: nil) }, excluded: [:])
    }
    private let dbtRendered: JSONValue = .object(["command": .string("npx"), "args": .array([.string("-y"), .string("@dbt/mcp")]),
                                                 "env": .object(["DBT_TOKEN": .string("${CC_NEEDS:DBT_TOKEN}")])])
    private let dbtFilled: JSONValue = .object(["command": .string("npx"), "args": .array([.string("-y"), .string("@dbt/mcp")]),
                                               "env": .object(["DBT_TOKEN": .string("tok-123")])])
    private var tokenNeed: [String: RenderedNeed] { ["DBT_TOKEN": RenderedNeed(hint: nil, pointer: JSONPointer(["env", "DBT_TOKEN"]))] }

    func testAFilledMarkerIsNotAChange() {
        let diff = CollectionDiff.pending(rendered: rendered(["dbt": (dbtRendered, tokenNeed)]),
                                          current: ["dbt": MCPEntry(enabled: true, config: dbtFilled)])
        XCTAssertTrue(diff.isEmpty)
    }

    func testAddedRemovedAndChangedAreNamedAndSorted() {
        let current = ["dbt": MCPEntry(enabled: false, config: dbtFilled),
                       "confluence": MCPEntry(config: .object(["command": .string("x")]))]
        var changedDbt = dbtRendered
        changedDbt = changedDbt.replacing(at: JSONPointer(["args", "1"]), with: .string("@dbt/mcp@2"))!
        let diff = CollectionDiff.pending(rendered: rendered(["dbt": (changedDbt, tokenNeed), "datadog": (.object(["command": .string("d")]), [:])]), current: current)
        XCTAssertEqual(diff.added, ["datadog"])
        XCTAssertEqual(diff.removed, ["confluence"])
        XCTAssertEqual(diff.changed, ["dbt"])
        XCTAssertEqual(diff.summary(), "adds datadog; removes confluence; changes dbt")
    }

    func testApplyCarriesAFilledValueToTheMarkersNewPlace() {
        // The author inserted an argument, so the marker moved from /args/0 to /args/1.
        let previous: [String: [String: CollectionsFile.Need]] = ["ledger": ["server_path": .init(hint: nil, pointer: JSONPointer(["args", "0"]))]]
        let current = ["ledger": MCPEntry(enabled: true, config: .object(["command": .string("node"), "args": .array([.string("/Users/d/ledger/index.js")])]))]
        let newConfig: JSONValue = .object(["command": .string("node"), "args": .array([.string("--inspect"), .string("${CC_NEEDS:server_path}")])])
        let result = CollectionApply.apply(rendered: rendered(["ledger": (newConfig, ["server_path": RenderedNeed(hint: "h", pointer: JSONPointer(["args", "1"]))])]),
                                           current: current, previousNeeds: previous)
        XCTAssertEqual(result.entries["ledger"]?.config.value(at: JSONPointer(["args", "1"])), .string("/Users/d/ledger/index.js"))
        XCTAssertEqual(result.entries["ledger"]?.enabled, true)
        XCTAssertEqual(result.needs["ledger"]?["server_path"], .init(hint: "h", pointer: JSONPointer(["args", "1"])))
    }

    func testApplyLeavesAnUnfilledMarkerAndDisablesNewConnectors() {
        let result = CollectionApply.apply(rendered: rendered(["dbt": (dbtRendered, tokenNeed)]), current: [:], previousNeeds: [:])
        XCTAssertEqual(result.entries["dbt"]?.config, dbtRendered)
        XCTAssertEqual(result.entries["dbt"]?.enabled, false)
    }

    func testApplyDropsConnectorsTheSourceRemoved() {
        let result = CollectionApply.apply(rendered: rendered([:]), current: ["gone": MCPEntry(config: .object([:]))], previousNeeds: [:])
        XCTAssertTrue(result.entries.isEmpty)
    }

    func testPendingIgnoresEnabledAndExcluded() {
        var r = rendered(["dbt": (dbtRendered, tokenNeed)])
        r.excluded = ["ledger": "reason"]
        let diff = CollectionDiff.pending(rendered: r, current: ["dbt": MCPEntry(enabled: false, config: dbtFilled)])
        XCTAssertTrue(diff.isEmpty)
    }

    // Two marker names can render into the same leaf, e.g. "${CC_NEEDS:a}-${CC_NEEDS:b}"; apply
    // must not let dictionary iteration order (unspecified on both platforms) decide which
    // filled value survives.
    func testTwoMarkersInOneLeafCarryTheSortedFirstFilledValue() {
        let previous: [String: [String: CollectionsFile.Need]] = [
            "multi": ["a": .init(hint: nil, pointer: JSONPointer(["args", "0"])),
                     "b": .init(hint: nil, pointer: JSONPointer(["args", "1"]))],
        ]
        let current = ["multi": MCPEntry(enabled: true, config: .object(["command": .string("c"),
                                                                         "args": .array([.string("alpha"), .string("beta")])]))]
        let newConfig: JSONValue = .object(["command": .string("c"), "args": .array([.string("${CC_NEEDS:a}-${CC_NEEDS:b}")])])
        let needs: [String: RenderedNeed] = [
            "a": RenderedNeed(hint: nil, pointer: JSONPointer(["args", "0"])),
            "b": RenderedNeed(hint: nil, pointer: JSONPointer(["args", "0"])),
        ]
        for _ in 0..<10 {
            let result = CollectionApply.apply(rendered: rendered(["multi": (newConfig, needs)]), current: current, previousNeeds: previous)
            XCTAssertEqual(result.entries["multi"]?.config.value(at: JSONPointer(["args", "0"])), .string("alpha"))
        }
    }

    func testAMissingOrNonStringCurrentLeafIsAChange() {
        let renderedConfig: JSONValue = .object(["command": .string("c"), "env": .object(["X": .string("${CC_NEEDS:X}")])])
        let needs: [String: RenderedNeed] = ["X": RenderedNeed(hint: nil, pointer: JSONPointer(["env", "X"]))]

        let noEnv = CollectionDiff.pending(rendered: rendered(["srv": (renderedConfig, needs)]),
                                           current: ["srv": MCPEntry(config: .object(["command": .string("c")]))])
        XCTAssertEqual(noEnv.changed, ["srv"])

        let numericEnv = CollectionDiff.pending(rendered: rendered(["srv": (renderedConfig, needs)]),
                                                current: ["srv": MCPEntry(config: .object(["command": .string("c"), "env": .object(["X": .int(5)])]))])
        XCTAssertEqual(numericEnv.changed, ["srv"])
    }

    func testAStalePreviousPointerLeavesTheMarker() {
        let previous: [String: [String: CollectionsFile.Need]] = ["ledger": ["server_path": .init(hint: nil, pointer: JSONPointer(["args", "3"]))]]
        let current = ["ledger": MCPEntry(enabled: true, config: .object(["command": .string("node"), "args": .array([.string("/Users/d/ledger/index.js")])]))]
        let newConfig: JSONValue = .object(["command": .string("node"), "args": .array([.string("${CC_NEEDS:server_path}")])])
        let result = CollectionApply.apply(rendered: rendered(["ledger": (newConfig, ["server_path": RenderedNeed(hint: nil, pointer: JSONPointer(["args", "0"]))])]),
                                           current: current, previousNeeds: previous)
        XCTAssertEqual(result.entries["ledger"]?.config.value(at: JSONPointer(["args", "0"])), .string("${CC_NEEDS:server_path}"))
    }

    func testAStillMarkeredValueIsNotCarried() {
        let previous: [String: [String: CollectionsFile.Need]] = ["ledger": ["server_path": .init(hint: nil, pointer: JSONPointer(["args", "0"]))]]
        let current = ["ledger": MCPEntry(enabled: true, config: .object(["command": .string("node"), "args": .array([.string("${CC_NEEDS:server_path}")])]))]
        let newConfig: JSONValue = .object(["command": .string("node"), "args": .array([.string("${CC_NEEDS:server_path}")])])
        let result = CollectionApply.apply(rendered: rendered(["ledger": (newConfig, ["server_path": RenderedNeed(hint: nil, pointer: JSONPointer(["args", "0"]))])]),
                                           current: current, previousNeeds: previous)
        XCTAssertEqual(result.entries["ledger"]?.config.value(at: JSONPointer(["args", "0"])), .string("${CC_NEEDS:server_path}"))
    }
}

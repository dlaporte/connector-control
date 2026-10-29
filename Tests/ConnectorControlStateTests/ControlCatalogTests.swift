import XCTest
import ConnectorControlCore
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ControlCatalogTests.cs
@MainActor
final class ControlCatalogTests: XCTestCase {
    /// The spec's table: read-only, destructive, idempotent, gate class, and the command's first word(s).
    private let table: [String: (Bool, Bool, Bool, ControlGateClass, String)] = [
        "get_status": (true, false, false, .read, "status"),
        "check_tools": (true, false, false, .read, "doctor"),
        "list_collections": (true, false, false, .read, "collections"),
        "list_connectors": (true, false, false, .read, "connectors"),
        "get_connector": (true, false, false, .read, "show <name>"),
        "get_pending_update": (true, false, false, .read, "updates <collection>"),
        "list_backups": (true, false, false, .read, "backups"),
        "get_settings": (true, false, false, .read, "settings"),
        "set_connector_enabled": (false, false, true, .direct, "enable <name> | disable <name>"),
        "switch_collection": (false, false, true, .direct, "use <collection>"),
        "create_collection": (false, false, false, .direct, "collection new <name>"),
        "rename_collection": (false, false, false, .direct, "collection rename <old> <new>"),
        "duplicate_collection": (false, false, false, .direct, "collection duplicate <name>"),
        "refresh_subscription": (false, false, false, .direct, "refresh <collection>"),
        "set_setting": (false, false, true, .direct, "settings set <key> <value>"),
        "add_connector": (false, false, false, .gated, "add <name>"),
        "subscribe": (false, false, false, .gated, "subscribe <file>"),
        "restart_claude": (false, false, false, .alwaysConfirm, "restart-claude"),
        "update_connector": (false, true, false, .gated, "edit <name>"),
        "delete_connectors": (false, true, false, .gated, "delete <name>…"),
        "delete_collection": (false, true, false, .gated, "collection delete <name>"),
        "import_document": (false, true, false, .gated, "import <file>"),
        "apply_update": (false, true, false, .gated, "apply-update <collection>"),
        "restore_backup": (false, true, false, .gated, "backups restore <id>"),
    ]

    func testTheCatalogIsTheSpecsTable() {
        XCTAssertEqual(ControlTools.catalog.map(\.name).sorted(), table.keys.sorted())
        XCTAssertEqual(Set(ControlTools.catalog.map(\.name)).count, ControlTools.catalog.count, "names are unique")
        for tool in ControlTools.catalog {
            guard let row = table[tool.name] else { return XCTFail(tool.name) }
            XCTAssertEqual(tool.annotations, ControlToolAnnotations(readOnly: row.0, destructive: row.1, idempotent: row.2, openWorld: false), tool.name)
            XCTAssertEqual(tool.gate, row.3, tool.name)
            XCTAssertTrue(tool.cliUsage.hasPrefix(row.4), tool.name)
        }
    }

    func testEveryToolDeclaresAllFourHints() {
        for tool in ControlTools.catalog {
            guard case .object(let descriptor) = tool.mcpDescriptor,
                  case .object(let hints)? = descriptor["annotations"] else { return XCTFail(tool.name) }
            XCTAssertEqual(Set(descriptor.keys), ["name", "title", "description", "inputSchema", "annotations"], tool.name)
            XCTAssertEqual(Set(hints.keys), ["readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint"], tool.name)
            XCTAssertTrue(hints.values.allSatisfy { if case .bool = $0 { return true } else { return false } }, tool.name)
            XCTAssertEqual(hints["openWorldHint"], .bool(false), tool.name)
        }
    }

    /// Claude's connector settings list a tool by its title, and by its bare name without one.
    func testEveryToolHasATitleThatIsNotItsName() {
        XCTAssertEqual(ControlTools.titles.keys.sorted(), ControlTools.catalog.map(\.name).sorted())
        for tool in ControlTools.catalog {
            XCTAssertFalse(tool.title.isEmpty, tool.name)
            XCTAssertNotEqual(tool.title, tool.name)
            XCTAssertEqual(tool.title, ControlTools.titles[tool.name], tool.name)
            XCTAssertEqual(tool.mcpDescriptor.value(at: JSONPointer(["title"])), .string(tool.title), tool.name)
        }
    }

    func testEverySchemaIsAnObjectAndDeclaresWhatItRequires() {
        for tool in ControlTools.catalog {
            guard case .object(let schema) = tool.inputSchema, schema["type"] == .string("object"),
                  case .object(let properties)? = schema["properties"] else { return XCTFail(tool.name) }
            if case .array(let required)? = schema["required"] {
                for case .string(let name) in required { XCTAssertNotNil(properties[name], "\(tool.name).\(name)") }
            }
        }
    }

    func testEveryToolHasAGroupHelpListsAndAOneSentenceSummary() {
        for tool in ControlTools.catalog {
            XCTAssertTrue(ControlTools.helpGroups.contains(tool.cliGroup), tool.name)
            XCTAssertFalse(tool.summary.isEmpty, tool.name)
            XCTAssertFalse(tool.summary.hasSuffix("."), tool.name)
            XCTAssertTrue(tool.description.hasPrefix(tool.summary), tool.name)
        }
    }

    /// Help is static data, so the command-line tool can print it without an app.
    func testHelpIsGeneratedFromTheToolListInItsGroups() {
        let lines = ControlTools.helpText().components(separatedBy: "\n")
        XCTAssertEqual(lines.first, ControlTools.helpUsage)
        XCTAssertEqual(lines.last, ControlTools.helpOptions)
        XCTAssertEqual(Array(lines.filter { !$0.hasPrefix(" ") }.dropFirst().dropLast()), ControlTools.helpGroups)
        // Every description starts in the same column (38, 1-based), counted in Unicode scalars.
        let prefix = 2 + ControlTools.helpColumn
        var rows = ControlTools.catalog.map { ($0.cliUsage, $0.summary) }
        rows.append((ControlTools.relayUsage, ControlTools.relaySummary))
        for (usage, summary) in rows {
            let matches = lines.indices.filter { lines[$0].hasSuffix(summary) && lines[$0].hasPrefix("  ") }
            XCTAssertEqual(matches.count, 1, usage)
            let index = matches[0]
            XCTAssertEqual(ControlText.width(lines[index]) - ControlText.width(summary), prefix, usage)
            if ControlText.width(usage) + 4 > prefix {
                XCTAssertEqual(lines[index - 1], "  " + usage, usage)
                XCTAssertEqual(lines[index], String(repeating: " ", count: prefix) + summary, usage)
            } else {
                XCTAssertEqual(lines[index], "  " + ControlText.pad(usage, to: ControlTools.helpColumn) + summary, usage)
            }
        }
        XCTAssertEqual(prefix, 37)
        let add = ControlTools.catalog.first { $0.cliUsage.hasPrefix("add ") }!
        XCTAssertTrue(lines.contains("  " + add.cliUsage))
        XCTAssertTrue(lines.contains(String(repeating: " ", count: 37) + add.summary))
        XCTAssertTrue(lines.contains("  connectors [--collection <name>]   List connectors and what each runs"))
        XCTAssertTrue(lines.contains("  mcp                                " + ControlTools.relaySummary))
    }
}

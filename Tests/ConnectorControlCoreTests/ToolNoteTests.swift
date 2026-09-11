import XCTest
@testable import ConnectorControlCore

final class ToolNoteTests: XCTestCase {
    func testMissingToolNoteCarriesTheInstallLinkAndCommand() {
        let note = ToolNote.make(tool: .npx, status: .notFound)
        XCTAssertEqual(note?.text, "npx wasn’t found, so Claude Desktop won’t be able to start this connector.")
        XCTAssertEqual(note?.linkTitle, "Install Node.js")
        XCTAssertEqual(note?.linkURL.absoluteString, "https://nodejs.org/en/download")
        XCTAssertEqual(note?.installCommand, "brew install node")
        let uv = ToolNote.make(tool: .uv, status: .notFound)
        XCTAssertEqual(uv?.text, "uv wasn’t found, so Claude Desktop won’t be able to start this connector.")
        XCTAssertEqual(uv?.linkTitle, "Install uv")
        XCTAssertEqual(uv?.linkURL.absoluteString, "https://docs.astral.sh/uv/getting-started/installation/")
        XCTAssertEqual(uv?.installCommand, "brew install uv")
        XCTAssertNil(note?.advice, "the advice line belongs to the shell-only state")
        XCTAssertNil(ToolNote.make(tool: .node, status: .found(path: "/usr/local/bin/node", version: "22.11.0")))
        XCTAssertNil(ToolNote.make(tool: .node, status: nil), "unknown is not a problem yet")
    }

    func testShellOnlyNoteNamesThePathAndAdvisesAVisibleInstall() {
        let path = "/Users/me/.nvm/versions/node/v22.11.0/bin/npx"
        let note = ToolNote.make(tool: .npx, status: .foundInShellOnly(path: path, version: "10.9.2"))
        XCTAssertEqual(note?.text, "npx is at \(path) in your shell, but Claude Desktop launches connectors with its own PATH and may not see it there.")
        XCTAssertEqual(note?.advice, "Install it with Homebrew or the official installer, which put it where Claude Desktop looks; version managers such as nvm install elsewhere.")
        XCTAssertEqual(note?.linkTitle, "Install Node.js")
        XCTAssertEqual(note?.installCommand, "brew install node",
                       "Homebrew installs into the PATH floor, so this fix clears the note")
        let uvx = ToolNote.make(tool: .uvx, status: .foundInShellOnly(path: "/Users/me/.local/bin/uvx", version: nil))
        XCTAssertEqual(uvx?.installCommand, "brew install uv")
        XCTAssertEqual(uvx?.advice, ToolNote.shellOnlyAdvice)
    }

    func testStatusTextForEachState() {
        XCTAssertEqual(ToolNote.statusText(nil), "Checking…")
        XCTAssertEqual(ToolNote.statusText(.found(path: "/x/npx", version: "10.9.2")), "10.9.2")
        XCTAssertEqual(ToolNote.statusText(.found(path: "/x/npx", version: nil)), "Found")
        XCTAssertEqual(ToolNote.statusText(.foundInShellOnly(path: "/x/npx", version: "10.9.2")), "Not visible to Claude Desktop")
        XCTAssertEqual(ToolNote.statusText(.notFound), "Not found")
    }

    /// Ordering, name mapping, and lookup — behavior StringCatalogTests
    /// doesn't cover (it pins strings, not enum order or lookup logic).
    func testOrderNameMappingAndFamilyLookup() {
        XCTAssertEqual(Tool.allCases, [.npx, .node, .uvx, .uv])
        XCTAssertEqual(Tool.allCases.map(\.name), ["npx", "node", "uvx", "uv"])
        XCTAssertEqual(Tool(name: "NPX"), .npx)
        XCTAssertNil(Tool(name: "python"))
        XCTAssertEqual(Tool.npx.family, .nodeJS)
        XCTAssertEqual(Tool.node.family, .nodeJS)
        XCTAssertEqual(Tool.uvx.family, .uv)
        XCTAssertEqual(Tool.uv.family, .uv)
    }

    func testRowWarningIsTheRowTooltipForEachStatus() {
        XCTAssertNil(ToolNote.rowWarning(tool: .npx, status: nil),
                     "unknown must never flash a glyph before the probe has answered")
        XCTAssertNil(ToolNote.rowWarning(tool: .npx, status: .found(path: "/opt/homebrew/bin/npx", version: "10.9.2")))
        XCTAssertNil(ToolNote.rowWarning(tool: .npx, status: .found(path: "/opt/homebrew/bin/npx", version: nil)))
        XCTAssertEqual(ToolNote.rowWarning(tool: .npx, status: .notFound),
                       "Needs npx, which wasn’t found. Edit to see how to install it.")
        XCTAssertEqual(ToolNote.rowWarning(tool: .uvx, status: .notFound),
                       "Needs uvx, which wasn’t found. Edit to see how to install it.")
        XCTAssertEqual(ToolNote.rowWarning(tool: .node,
                                           status: .foundInShellOnly(path: "/Users/me/.nvm/bin/node", version: "22.11.0")),
                       "Needs node, which Claude Desktop may not see. Edit to see how to fix it.")
        XCTAssertEqual(ToolNote.rowMissingText(.uv),
                       "Needs uv, which wasn’t found. Edit to see how to install it.")
        XCTAssertEqual(ToolNote.rowShellOnlyText(.uv),
                       "Needs uv, which Claude Desktop may not see. Edit to see how to fix it.")
    }
}

import XCTest
@testable import ConnectorControlCore

final class PasteRecoveryTests: XCTestCase {
    private func command(_ v: JSONValue?) -> String? {
        guard case .object(let o)? = v, case .string(let s)? = o["command"] else { return nil }
        return s
    }

    func testBareStanzaWithTrailingBrace() {
        // Exactly the shape copied out of an mcpServers block: a bare
        // "name": {…} property plus the block's leftover closing brace.
        let text = """
          "okta-mcp-server": {
            "command": "/opt/homebrew/bin/uv",
            "args": ["run", "--directory", "/x", "okta-mcp-server"],
            "env": { "OKTA_ORG_URL": "https://example.okta.com" }
          }
        }
        """
        let r = PasteRecovery.recover(text)
        XCTAssertEqual(r?.name, "okta-mcp-server")
        XCTAssertEqual(command(r?.config), "/opt/homebrew/bin/uv")
    }

    func testBareStanzaWithoutTrailingBrace() {
        let text = "\"foo\": {\"command\": \"npx\"}"
        let r = PasteRecovery.recover(text)
        XCTAssertEqual(r?.name, "foo")
        XCTAssertEqual(command(r?.config), "npx")
    }

    func testMcpServersWrapper() {
        let text = "{\"mcpServers\": {\"bar\": {\"command\": \"uvx\"}}}"
        let r = PasteRecovery.recover(text)
        XCTAssertEqual(r?.name, "bar")
        XCTAssertEqual(command(r?.config), "uvx")
    }

    func testSingleEntryNameWrapper() {
        let text = "{\"baz\": {\"command\": \"node\"}}"
        let r = PasteRecovery.recover(text)
        XCTAssertEqual(r?.name, "baz")
        XCTAssertEqual(command(r?.config), "node")
    }

    func testPlainConfigObjectIsNotRenamed() {
        let text = "{\"command\": \"npx\", \"args\": [\"-y\", \"pkg\"]}"
        let r = PasteRecovery.recover(text)
        XCTAssertNil(r?.name, "a bare config must not be treated as name-wrapped")
        XCTAssertEqual(command(r?.config), "npx")
    }

    func testSingleKeyConfigNotUnwrapped() {
        // {"command": "x"} is a one-key CONFIG, not a {name: config} wrapper.
        let r = PasteRecovery.recover("{\"command\": \"x\"}")
        XCTAssertNil(r?.name)
        XCTAssertEqual(command(r?.config), "x")
    }

    func testBracesInsideStringValuesAreIgnored() {
        let text = "\"weird\": {\"command\": \"echo }}}\"}"
        let r = PasteRecovery.recover(text)
        XCTAssertEqual(r?.name, "weird")
        XCTAssertEqual(command(r?.config), "echo }}}")
    }

    func testMultiEntryMcpServersNotMisnamed() {
        let text = "{\"mcpServers\": {\"a\": {\"command\":\"x\"}, \"b\": {\"command\":\"y\"}}}"
        let r = PasteRecovery.recover(text)
        XCTAssertNotEqual(r?.name, "mcpServers",
                          "a multi-entry wrapper must not be unwrapped to name=mcpServers")
    }

    func testGarbageReturnsNil() {
        XCTAssertNil(PasteRecovery.recover("{{{ not json"))
        XCTAssertNil(PasteRecovery.recover("   "))
    }
}

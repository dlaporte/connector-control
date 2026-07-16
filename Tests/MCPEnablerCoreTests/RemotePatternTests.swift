import XCTest
@testable import MCPEnablerCore

final class RemotePatternTests: XCTestCase {
    private func config(command: String = "npx", args: [String]) -> JSONValue {
        .object(["command": .string(command),
                 "args": .array(args.map(JSONValue.string))])
    }

    func testDetectsCanonicalPattern() {
        XCTAssertEqual(
            RemotePattern.detect(config(
                args: ["-y", "mcp-remote", "https://example.com/mcp"])),
            "https://example.com/mcp")
    }

    func testDetectsPatternWithoutDashY() {
        XCTAssertEqual(
            RemotePattern.detect(config(args: ["mcp-remote", "https://x.dev/mcp"])),
            "https://x.dev/mcp")
    }

    func testExtraKeysDoNotDisqualify() {
        let value = JSONValue.object([
            "command": .string("npx"),
            "args": .array([.string("-y"), .string("mcp-remote"),
                            .string("https://x.dev/mcp")]),
            "env": .object(["TOKEN": .string("abc")]),
        ])
        XCTAssertEqual(RemotePattern.detect(value), "https://x.dev/mcp")
    }

    func testRejectsWrongCommand() {
        XCTAssertNil(RemotePattern.detect(config(
            command: "node", args: ["-y", "mcp-remote", "https://x.dev/mcp"])))
    }

    func testRejectsExtraArgs() {
        XCTAssertNil(RemotePattern.detect(config(
            args: ["-y", "mcp-remote", "https://x.dev/mcp", "--debug"])))
    }

    func testRejectsNonURL() {
        XCTAssertNil(RemotePattern.detect(config(args: ["-y", "mcp-remote", "not a url"])))
        XCTAssertNil(RemotePattern.detect(config(args: ["-y", "mcp-remote", "ftp://x.dev"])))
    }

    func testRejectsMissingArgsOrNonStringArgs() {
        XCTAssertNil(RemotePattern.detect(.object(["command": .string("npx")])))
        XCTAssertNil(RemotePattern.detect(.object([
            "command": .string("npx"),
            "args": .array([.string("mcp-remote"), .int(42)]),
        ])))
    }

    func testMakeBuildsCanonicalConfig() {
        XCTAssertEqual(
            RemotePattern.make(url: "https://x.dev/mcp"),
            .object(["command": .string("npx"),
                     "args": .array([.string("-y"), .string("mcp-remote"),
                                     .string("https://x.dev/mcp")])]))
    }

    func testMakeThenDetectRoundTrips() {
        XCTAssertEqual(
            RemotePattern.detect(RemotePattern.make(url: "https://x.dev/mcp")),
            "https://x.dev/mcp")
    }
}

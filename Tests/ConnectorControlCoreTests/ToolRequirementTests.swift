import XCTest
@testable import ConnectorControlCore

final class ToolRequirementTests: XCTestCase {
    func testRecognisesTheFourToolsByBasename() {
        XCTAssertEqual(ToolRequirement.requiredTool(command: "npx", args: ["-y", "mcp-remote", "https://x.dev/mcp"]), .npx)
        XCTAssertEqual(ToolRequirement.requiredTool(command: "node", args: ["server.js"]), .node)
        XCTAssertEqual(ToolRequirement.requiredTool(command: "uvx", args: ["mcp-server-fetch"]), .uvx)
        XCTAssertEqual(ToolRequirement.requiredTool(command: "uv", args: ["run", "server.py"]), .uv)
        XCTAssertNil(ToolRequirement.requiredTool(command: "python", args: []))
        XCTAssertNil(ToolRequirement.requiredTool(command: "", args: []))
        XCTAssertNil(ToolRequirement.requiredTool(command: "   ", args: []))
    }

    func testIgnoresCmdAndExeSuffixesAndCase() {
        XCTAssertEqual(ToolRequirement.requiredTool(command: "NPX.CMD", args: []), .npx)
        XCTAssertEqual(ToolRequirement.requiredTool(command: "node.exe", args: []), .node)
        XCTAssertEqual(ToolRequirement.requiredTool(command: " Uvx ", args: []), .uvx)
        XCTAssertNil(ToolRequirement.requiredTool(command: "npx.cmd.exe", args: []),
                     "only one suffix is stripped")
    }

    func testUnwrapsOneCmdSlashC() {
        XCTAssertEqual(ToolRequirement.requiredTool(
            command: "cmd", args: ["/c", "npx", "-y", "mcp-remote", "https://x.dev/mcp"]), .npx)
        XCTAssertEqual(ToolRequirement.requiredTool(command: "cmd.exe", args: ["/C", "uvx"]), .uvx)
        XCTAssertNil(ToolRequirement.requiredTool(command: "cmd", args: ["/c"]))
        XCTAssertNil(ToolRequirement.requiredTool(command: "cmd", args: ["/k", "npx"]))
        XCTAssertNil(ToolRequirement.requiredTool(command: "cmd", args: ["/c", "cmd", "/c", "npx"]),
                     "one level only")
    }

    func testLeavesPathsAlone() {
        XCTAssertNil(ToolRequirement.requiredTool(command: "/usr/local/bin/npx", args: []))
        XCTAssertNil(ToolRequirement.requiredTool(command: "C:\\Program Files\\nodejs\\npx.cmd", args: []))
        XCTAssertNil(ToolRequirement.requiredTool(command: "./node", args: []))
        XCTAssertNil(ToolRequirement.requiredTool(command: "cmd", args: ["/c", "/opt/homebrew/bin/npx"]))
    }

    func testConfigOverloadReadsCommandAndArgs() {
        XCTAssertEqual(ToolRequirement.requiredTool(for: RemotePattern.make(url: "https://x.dev/mcp")), .npx)
        XCTAssertEqual(ToolRequirement.requiredTool(for: .object([
            "command": .string("cmd"), "args": .array([.string("/c"), .string("npx")])])), .npx)
        XCTAssertEqual(ToolRequirement.requiredTool(for: .object(["command": .string("node")])), .node)
        XCTAssertNil(ToolRequirement.requiredTool(for: .object(["args": .array([.string("npx")])])))
        XCTAssertNil(ToolRequirement.requiredTool(for: .object([
            "command": .string("cmd"), "args": .array([.string("/c"), .int(42)])])),
            "a non-string arg empties the args")
        XCTAssertNil(ToolRequirement.requiredTool(for: .string("npx")))
    }

    func testRequiredToolsAcrossConfigsIsDedupedAndOrdered() {
        let configs: [JSONValue] = [
            RemotePattern.make(url: "https://a.dev/mcp"),                                   // npx
            RemotePattern.make(url: "https://b.dev/mcp"),                                   // npx again
            .object(["command": .string("uvx"), "args": .array([.string("some-server")])]),
            .object(["command": .string("/usr/local/bin/node")]),                           // a path: no tool
            .object(["command": .string("python")]),                                        // not one of the four
            .string("not an object"),
        ]
        XCTAssertEqual(ToolRequirement.requiredTools(for: configs), [.npx, .uvx])
        XCTAssertEqual(ToolRequirement.requiredTools(for: []), [])
        XCTAssertEqual(ToolRequirement.requiredTools(for: [.object(["command": .string("python")])]), [])
        XCTAssertEqual(ToolRequirement.requiredTools(for: [
            .object(["command": .string("uv")]),
            .object(["command": .string("node")]),
        ]), [.node, .uv], "Tool.allCases order, not the configs' order")
    }
}

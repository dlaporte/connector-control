import XCTest
import ConnectorControlTestSupport
@testable import ConnectorControlCore

final class ClaudeConfigIOTests: XCTestCase {
    var tempDir: TempDir!
    var dir: URL!
    var url: URL { dir.appendingPathComponent("claude_desktop_config.json") }

    override func setUpWithError() throws {
        tempDir = TempDir(prefix: "claude")
        dir = tempDir.url
    }

    override func tearDownWithError() throws {
        tempDir.dispose()
    }

    private func write(_ s: String) throws { try Data(s.utf8).write(to: url) }

    private func rootObject() throws -> [String: Any] {
        try XCTUnwrap(JSONSerialization.jsonObject(
            with: Data(contentsOf: url)) as? [String: Any])
    }

    func testReadRealisticConfig() throws {
        try write(Fixtures.realisticClaudeConfig)
        let servers = try ClaudeConfigIO.readMCPServers(at: url)
        XCTAssertEqual(Set(servers.keys), ["scoutbook", "aws-mcp", "service-now"])
        XCTAssertEqual(servers["scoutbook"], RemotePattern.make(url: "https://scoutbook.example.com/mcp"))
    }

    func testReadMissingFileReturnsEmpty() throws {
        XCTAssertEqual(try ClaudeConfigIO.readMCPServers(at: url), [:])
    }

    func testReadAbsentKeyReturnsEmpty() throws {
        try write(#"{"preferences": {}}"#)
        XCTAssertEqual(try ClaudeConfigIO.readMCPServers(at: url), [:])
    }

    func testReadMalformedFileThrows() throws {
        try write("{oops")
        XCTAssertThrowsError(try ClaudeConfigIO.readMCPServers(at: url)) {
            guard case ClaudeConfigError.malformed = $0 else {
                return XCTFail("wrong error: \($0)")
            }
        }
    }

    func testReadNonObjectMCPServersThrows() throws {
        try write(#"{"mcpServers": "surprise"}"#)
        XCTAssertThrowsError(try ClaudeConfigIO.readMCPServers(at: url))
    }

    func testWritePreservesEveryOtherKeyByValue() throws {
        try write(Fixtures.realisticClaudeConfig)
        let before = try rootObject()
        try ClaudeConfigIO.write(
            mcpServers: ["only-one": .object(["command": .string("echo")])], to: url)
        let after = try rootObject()
        XCTAssertEqual(Set(after.keys), Set(before.keys))
        for key in before.keys where key != "mcpServers" {
            XCTAssertEqual(
                try JSONSerialization.data(withJSONObject: ["v": after[key]!],
                                           options: [.sortedKeys]),
                try JSONSerialization.data(withJSONObject: ["v": before[key]!],
                                           options: [.sortedKeys]),
                "key \(key) changed")
        }
        let servers = try XCTUnwrap(after["mcpServers"] as? [String: Any])
        XCTAssertEqual(Array(servers.keys), ["only-one"])
    }

    func testWriteToMissingFileCreatesIt() throws {
        try ClaudeConfigIO.write(
            mcpServers: ["a": .object(["command": .string("x")])], to: url)
        XCTAssertEqual(Set(try rootObject().keys), ["mcpServers"])
    }

    func testReadEmptyFileReturnsEmptyServers() throws {
        try write("")
        XCTAssertEqual(try ClaudeConfigIO.readMCPServers(at: url), [:])
    }

    func testWriteToEmptyFileRecreatesConfig() throws {
        try write("")
        try ClaudeConfigIO.write(
            mcpServers: ["a": .object(["command": .string("x")])], to: url)
        XCTAssertEqual(Set(try rootObject().keys), ["mcpServers"])
        XCTAssertEqual(Set(try ClaudeConfigIO.readMCPServers(at: url).keys), ["a"])
    }

    func testWriteRefusesMalformedFile() throws {
        try write("{oops")
        XCTAssertThrowsError(try ClaudeConfigIO.write(mcpServers: [:], to: url))
        XCTAssertEqual(try String(contentsOf: url, encoding: .utf8), "{oops")
    }

    func testDisabledSubsetOmittedAndReadBack() throws {
        try write(Fixtures.realisticClaudeConfig)
        var servers = try ClaudeConfigIO.readMCPServers(at: url)
        servers.removeValue(forKey: "aws-mcp")
        try ClaudeConfigIO.write(mcpServers: servers, to: url)
        XCTAssertEqual(Set(try ClaudeConfigIO.readMCPServers(at: url).keys),
                       ["scoutbook", "service-now"])
    }
}

import XCTest
import ConnectorControlTestSupport
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/ClaudeConfigIOTests.cs
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
        XCTAssertThrowsError(try ClaudeConfigIO.readMCPServers(at: url)) {
            XCTAssertEqual($0 as? ClaudeConfigError, .malformed("mcpServers is not a JSON object"))
        }
    }

    /// windows/tests/ConnectorControl.Core.Tests/ClaudeConfigIOTests.cs ReadNonObjectTopLevelThrows.
    func testReadNonObjectTopLevelThrows() throws {
        try write("[1, 2]")
        XCTAssertThrowsError(try ClaudeConfigIO.readMCPServers(at: url)) {
            XCTAssertEqual($0 as? ClaudeConfigError, .malformed("top level is not a JSON object"))
        }
    }

    func testWritePreservesEveryOtherKeyByValue() throws {
        try write(Fixtures.realisticClaudeConfig)
        let before = try rootObject()
        try ClaudeConfigIO.write(
            mcpServers: ["only-one": .object(["command": .string("echo")])], to: url)
        let after = try rootObject()
        XCTAssertEqual(Set(after.keys), Set(before.keys))
        for key in before.keys where key != "mcpServers" {
            XCTAssertEqual(JSONValue(any: after[key]!), JSONValue(any: before[key]!), "key \(key) changed")
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

    /// A key the top level or mcpServers names twice — exactly, or in two spellings Unicode counts as
    /// the same text — is refused: this app's parser keeps one and Claude's may keep the other.
    func testARepeatedKeyIsRefusedExactlyOrAsAnEquivalentSpelling() {
        let repeated = [
            #"{"x": 1, "x": 2}"#,
            #"{"mcpServers": {"a": {}, "a": {}}}"#,
            "{\"mcpServers\": {\"caf\u{E9}\": {}, \"cafe\u{301}\": {}}}",
            #"{"mcpServers": {"caf\u00e9": {}, "cafe\u0301": {}}}"#,
            #"{"mcpServers": {"\u0061": {}, "a": {}}}"#,
            "\u{FEFF}{\"mcpServers\": {\"a\": {}, \"a\": {}}}",
        ]
        for json in repeated {
            XCTAssertThrowsError(try ClaudeConfigIO.requireUniqueKeys(in: Data(json.utf8)), json) {
                XCTAssertEqual($0 as? ClaudeConfigError, .malformed(ClaudeConfigIO.repeatedKeyError))
            }
        }
    }

    /// Below mcpServers' own names, and inside a string, nothing is refused: the file is written as it
    /// was parsed, so what Claude reads there is what this app read.
    func testOnlyTheTopLevelAndTheServersNamesAreChecked() {
        let unique = [
            #"{"mcpServers": {"a": {"env": {"K": "1", "K": "2"}}}}"#,
            #"{"mcpServers": {"a": {}, "b": {}}, "other": {"a": 1, "a": 2}}"#,
            #"{"k": "{\"a\":1,\"a\":2}", "mcpServers": {"a": [{"b": 1}, {"b": 2}]}, "m": ["x", "x"]}"#,
            #"{"mcpServers": "a", "other": {"mcpServers": {"a": 1, "a": 2}}}"#,
            "",
        ]
        for json in unique {
            XCTAssertNoThrow(try ClaudeConfigIO.requireUniqueKeys(in: Data(json.utf8)), json)
        }
    }
}

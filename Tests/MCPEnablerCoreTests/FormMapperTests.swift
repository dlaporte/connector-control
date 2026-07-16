import XCTest
@testable import MCPEnablerCore

final class FormMapperTests: XCTestCase {
    func testCleanLocalConfigIsLossless() {
        let analysis = FormMapper.analyze(.object([
            "command": .string("npx"),
            "args": .array([.string("-y"), .string("pkg")]),
            "env": .object(["KEY": .string("v")]),
        ]))
        XCTAssertTrue(analysis.isLossless)
        XCTAssertEqual(analysis.model,
                       FormModel(command: "npx", args: ["-y", "pkg"],
                                 env: ["KEY": "v"], additional: [:]))
    }

    func testUnknownKeysArePreservedNotLost() {
        let analysis = FormMapper.analyze(.object([
            "command": .string("x"),
            "type": .string("http"),
            "headers": .object(["Authorization": .string("Bearer t")]),
        ]))
        XCTAssertTrue(analysis.isLossless)
        XCTAssertEqual(Set(analysis.model.additional.keys), ["type", "headers"])
    }

    func testStructuralViolationsAreListedAsLost() {
        let analysis = FormMapper.analyze(.object([
            "command": .int(5),
            "args": .array([.string("ok"), .object([:]), .int(3)]),
            "env": .object(["GOOD": .string("y"), "BAD": .int(1)]),
        ]))
        XCTAssertFalse(analysis.isLossless)
        XCTAssertEqual(analysis.lost, ["args[1] (object)", "args[2] (number)",
                                       "command (number)", "env.BAD (number)"])
        XCTAssertEqual(analysis.model.args, ["ok"])
        XCTAssertEqual(analysis.model.env, ["GOOD": "y"])
        XCTAssertEqual(analysis.model.command, "")
    }

    func testNonObjectArgsOrEnvAreLost() {
        let analysis = FormMapper.analyze(.object([
            "command": .string("x"),
            "args": .string("not an array"),
            "env": .array([]),
        ]))
        XCTAssertEqual(analysis.lost, ["args (not an array)", "env (not an object)"])
    }

    func testNonObjectConfigIsEntirelyLost() {
        let analysis = FormMapper.analyze(.string("weird"))
        XCTAssertEqual(analysis.lost, ["entire configuration (not a JSON object)"])
    }

    func testSerializeRoundTripsLosslessConfig() {
        let original = JSONValue.object([
            "command": .string("npx"),
            "args": .array([.string("-y"), .string("pkg")]),
            "env": .object(["K": .string("v")]),
            "headers": .object(["H": .string("x")]),
        ])
        let analysis = FormMapper.analyze(original)
        XCTAssertEqual(FormMapper.serialize(analysis.model), original)
    }

    func testSerializeOmitsEmptyArgsAndEnv() {
        let value = FormMapper.serialize(
            FormModel(command: "swift", args: [], env: [:], additional: [:]))
        XCTAssertEqual(value, .object(["command": .string("swift")]))
    }

    func testCommandlessConfigRoundTripsWithoutInjectingCommand() {
        let original = JSONValue.object([
            "type": .string("http"),
            "url": .string("https://example.com/mcp"),
        ])
        let analysis = FormMapper.analyze(original)
        XCTAssertTrue(analysis.isLossless)
        XCTAssertEqual(FormMapper.serialize(analysis.model), original)
    }
}

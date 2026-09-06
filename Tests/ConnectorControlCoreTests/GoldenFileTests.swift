import XCTest
@testable import ConnectorControlCore

/// The golden files under Tests/Fixtures/golden are Apple Foundation's output for
/// each input, produced by the REAL Core module. The Windows test suite asserts
/// its writer reproduces them byte for byte.
///
/// - `testGoldensAreCurrent` (always on) fails when the committed goldens no
///   longer match what this machine's Foundation produces.
/// - `testRegenerateGoldens` rewrites them; it runs only when
///   `CONNECTOR_CONTROL_UPDATE_GOLDENS=1` is set:
///     CONNECTOR_CONTROL_UPDATE_GOLDENS=1 swift test --filter GoldenFileTests
final class GoldenFileTests: XCTestCase {
    private var goldenDir: URL { Fixtures.url("golden") }
    private var inputsDir: URL { goldenDir.appendingPathComponent("inputs") }

    /// (folder name, bytes) for every output format, in a fixed order.
    private func outputs(for value: JSONValue) throws -> [(String, Data)] {
        [
            ("encoder", try value.serialized()),
            ("editor", Data(value.editorText().utf8)),
            ("serialization", try JSONSerialization.data(
                withJSONObject: value.anyValue, options: [.prettyPrinted, .sortedKeys])),
        ]
    }

    private func inputNames() throws -> [String] {
        try FileManager.default.contentsOfDirectory(atPath: inputsDir.path)
            .filter { $0.hasSuffix(".json") }
            .sorted()
    }

    func testRegenerateGoldens() throws {
        try XCTSkipUnless(
            ProcessInfo.processInfo.environment["CONNECTOR_CONTROL_UPDATE_GOLDENS"] == "1",
            "set CONNECTOR_CONTROL_UPDATE_GOLDENS=1 to rewrite the golden files")
        let fm = FileManager.default
        for name in try inputNames() {
            let value = try JSONValue.parse(try Data(contentsOf: inputsDir.appendingPathComponent(name)))
            for (dir, bytes) in try outputs(for: value) {
                let outDir = goldenDir.appendingPathComponent(dir)
                try fm.createDirectory(at: outDir, withIntermediateDirectories: true)
                try bytes.write(to: outDir.appendingPathComponent(name))
            }
        }
    }

    func testGoldensAreCurrent() throws {
        let names = try inputNames()
        XCTAssertEqual(names.count, 4, "expected the four golden inputs")
        for name in names {
            let value = try JSONValue.parse(try Data(contentsOf: inputsDir.appendingPathComponent(name)))
            for (dir, bytes) in try outputs(for: value) {
                let golden = goldenDir.appendingPathComponent(dir).appendingPathComponent(name)
                let committed = try Data(contentsOf: golden)
                XCTAssertEqual(
                    String(decoding: committed, as: UTF8.self),
                    String(decoding: bytes, as: UTF8.self),
                    "\(dir)/\(name) is stale — regenerate with CONNECTOR_CONTROL_UPDATE_GOLDENS=1")
            }
        }
    }
}

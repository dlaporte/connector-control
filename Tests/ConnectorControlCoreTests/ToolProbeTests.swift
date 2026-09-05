import XCTest
@testable import ConnectorControlCore

final class ToolProbeTests: XCTestCase {
    private var dir: URL!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("toolprobe-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    /// A stub launcher: an executable shell script named `name` in `sub`.
    @discardableResult
    private func stub(_ name: String, in sub: String, body: String = "echo 10.9.2") throws -> URL {
        let folder = dir.appendingPathComponent(sub)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appendingPathComponent(name)
        try "#!/bin/sh\n\(body)\n".write(to: file, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: file.path)
        return file
    }

    /// `floor` stands in for Claude Desktop's PATH floor (`/opt/homebrew/bin` and friends);
    /// tests pass their own temp directories so a real Homebrew install cannot leak in.
    private func probe(path: String, floor: [String] = [],
                       shell: @escaping @Sendable () -> String? = { nil },
                       timeout: TimeInterval = 2) -> ToolProbe {
        ToolProbe(environment: ["PATH": path], shellPath: shell,
                  claudePathFloor: floor, versionTimeout: timeout)
    }

    private var bin: String { dir.appendingPathComponent("bin").path }

    func testParseVersionStripsTheNameAndALeadingV() {
        XCTAssertEqual(ToolProbe.parseVersion("v22.11.0\n", tool: .node), "22.11.0")
        XCTAssertEqual(ToolProbe.parseVersion("10.9.2\r\n", tool: .npx), "10.9.2")
        XCTAssertEqual(ToolProbe.parseVersion("uv 0.4.30 (Homebrew 2024-11-20)", tool: .uv), "0.4.30")
        XCTAssertEqual(ToolProbe.parseVersion("uvx 0.4.30", tool: .uvx), "0.4.30")
        XCTAssertEqual(ToolProbe.parseVersion("\n  \nv1.2.3 extra", tool: .node), "1.2.3")
        XCTAssertEqual(ToolProbe.parseVersion("vanilla", tool: .node), "vanilla",
                       "a leading v is dropped only before a digit")
        XCTAssertNil(ToolProbe.parseVersion("", tool: .node))
        XCTAssertNil(ToolProbe.parseVersion("\n  \n", tool: .npx))
    }

    func testResolveNeedsAnExecutableRegularFile() throws {
        let exe = try stub("npx", in: "bin")
        let plain = dir.appendingPathComponent("bin/node")
        try "not executable".write(to: plain, atomically: true, encoding: .utf8)
        try FileManager.default.createDirectory(
            at: dir.appendingPathComponent("bin/uvx"), withIntermediateDirectories: true)
        let path = "\(dir.path)/missing:\(bin)"
        XCTAssertEqual(ToolProbe.resolve("npx", searchPath: path), exe.path)
        XCTAssertNil(ToolProbe.resolve("node", searchPath: path), "no execute bit")
        XCTAssertNil(ToolProbe.resolve("uvx", searchPath: path), "a directory is not a tool")
        XCTAssertNil(ToolProbe.resolve("uv", searchPath: path))
        XCTAssertNil(ToolProbe.resolve("npx", searchPath: ""))
    }

    func testProbeFindsAStubAndReadsItsVersion() throws {
        let exe = try stub("npx", in: "bin")
        XCTAssertEqual(probe(path: bin).probe(.npx), .found(path: exe.path, version: "10.9.2"))
        XCTAssertEqual(probe(path: bin).probe(.uvx), .notFound)
    }

    func testAToolInClaudesPathFloorIsVisible() throws {
        XCTAssertEqual(ToolProbe.defaultClaudePathFloor,
                       ["/usr/local/bin", "/opt/homebrew/bin", "/opt/homebrew/sbin"])
        let exe = try stub("npx", in: "floorbin")
        let floorDir = dir.appendingPathComponent("floorbin").path
        let calls = Counter()
        let probe = probe(path: dir.appendingPathComponent("empty").path,
                          floor: [floorDir], shell: { calls.bump(); return floorDir })
        XCTAssertEqual(probe.probe(.npx), .found(path: exe.path, version: "10.9.2"),
                       "Claude Desktop adds its PATH floor itself, so this is state A")
        XCTAssertEqual(calls.value, 0, "nothing was missing: the shell was never asked")
        XCTAssertEqual(probe.probe(.node), .notFound, "the floor directory holds npx only")
        XCTAssertEqual(calls.value, 1)
    }

    func testProbeFallsBackToTheShellPath() throws {
        let exe = try stub("node", in: "shellbin", body: "echo v22.11.0")
        let shellDir = dir.appendingPathComponent("shellbin").path
        let status = probe(path: dir.appendingPathComponent("empty").path, shell: { shellDir }).probe(.node)
        XCTAssertEqual(status, .foundInShellOnly(path: exe.path, version: "22.11.0"))
    }

    func testShellIsConsultedOnceAndOnlyWhenSomethingIsMissing() throws {
        try stub("npx", in: "bin")
        try stub("node", in: "bin")
        let calls = Counter()
        let shellDir = dir.appendingPathComponent("shellbin").path
        let allFound = probe(path: bin, shell: { calls.bump(); return shellDir })
        _ = allFound.probe([.npx, .node])
        XCTAssertEqual(calls.value, 0, "everything on the app PATH: the shell is never asked")
        let results = allFound.probe(Tool.allCases)
        XCTAssertEqual(calls.value, 1, "one batch, one shell lookup")
        XCTAssertEqual(results.count, 4)
        XCTAssertEqual(results[.uvx], .notFound)
        XCTAssertEqual(results[.uv], .notFound)
        let silent = probe(path: dir.appendingPathComponent("empty").path, shell: { nil })
        XCTAssertEqual(silent.probe(.uv), .notFound, "no shell PATH at all is just not found")
    }

    func testVersionCallTimesOutToVersionUnknown() throws {
        let exe = try stub("uv", in: "bin", body: "exec sleep 5")
        let started = Date()
        let status = probe(path: bin, timeout: 0.2).probe(.uv)
        XCTAssertEqual(status, .found(path: exe.path, version: nil))
        XCTAssertLessThan(Date().timeIntervalSince(started), 3,
                          "the hung version call was abandoned, not waited for")
    }

    func testNeverThrowsOnGarbage() throws {
        let garbage = probe(path: "::/nonexistent dir with spaces:/dev/null:\(dir.path)/missing")
        XCTAssertEqual(garbage.probe(.npx), .notFound)
        XCTAssertEqual(
            ToolProbe(environment: [:], shellPath: { nil }, claudePathFloor: []).probe(.node),
            .notFound)
        let broken = try stub("uvx", in: "bin", body: "exit 3")
        XCTAssertEqual(probe(path: bin).probe(.uvx), .found(path: broken.path, version: nil),
                       "a launcher that exits without printing is still found")
    }
}

/// A call counter a `@Sendable` closure may bump.
private final class Counter: @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0
    var value: Int { lock.withLock { count } }
    func bump() { lock.withLock { count += 1 } }
}

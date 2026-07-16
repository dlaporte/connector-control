# MCP Enabler Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A native SwiftUI macOS menu bar app that enables/disables/edits/adds/removes the MCP servers in Claude Desktop's `claude_desktop_config.json`, with a tool-owned master store, timestamped backups, and reconciliation against external changes.

**Architecture:** Two SwiftPM targets. `MCPEnablerCore` is a pure library (models, JSON round-tripping, master store, backups, reconciliation, remote-pattern detection, form representability, apply) — fully unit-tested with no UI. `MCPEnabler` is a thin SwiftUI `MenuBarExtra` app over it. The master store (`~/Library/Application Support/MCP Enabler/mcps.json`) is the source of truth; Claude's config is output, rewritten on Apply with every non-`mcpServers` key preserved by value.

**Tech Stack:** Swift 5.10+, SwiftPM, SwiftUI (`MenuBarExtra`, macOS 14+), Foundation `JSONSerialization`/`Codable`, XCTest. No third-party dependencies.

## Global Constraints

- Platform: macOS 14+, Swift tools 5.10, SwiftPM only — **no** Xcode project, **no** third-party packages.
- The spec is `docs/superpowers/specs/2026-07-16-mcp-enabler-design.md`; on any ambiguity, the spec wins.
- Never write to the real `~/Library/Application Support/Claude/claude_desktop_config.json` from tests or dev runs. Tests use temp dirs; dev runs use the `MCP_ENABLER_CLAUDE_CONFIG` / `MCP_ENABLER_STORE_DIR` env overrides (Task 2) pointing into `.sandbox/` (gitignored).
- Every file write in the product goes through `AtomicFile.write` (temp file + rename).
- Every write to Claude's config or `mcps.json` is preceded by a timestamped backup.
- All Claude-config keys other than `mcpServers` are preserved **by value** (formatting/key order may normalize).
- Backup retention: newest 20 per series; `claude_desktop_config.original.json` is never pruned.
- Test command: `swift test --filter <TestClass>`. Full suite must pass before every commit.
- App copy (UI strings) for the lossy-toggle dialog: buttons **"Stay in JSON"** (default) and **"Switch Anyway"**.

---

### Task 1: Package scaffold + JSONValue

**Files:**
- Create: `Package.swift`
- Create: `Sources/MCPEnablerCore/JSONValue.swift`
- Create: `Sources/MCPEnabler/main.swift` (placeholder so the package builds; replaced in Task 10)
- Test: `Tests/MCPEnablerCoreTests/JSONValueTests.swift`

**Interfaces:**
- Consumes: nothing.
- Produces: `enum JSONValue: Equatable, Hashable, Codable` with cases `.null, .bool(Bool), .int(Int), .double(Double), .string(String), .array([JSONValue]), .object([String: JSONValue])`; `static func parse(_ data: Data) throws -> JSONValue`; `func serialized() throws -> Data` (pretty, sorted keys); `var anyValue: Any`; `init(any: Any) throws`; `var typeName: String`; `enum JSONValueError: Error { case unsupported(String) }`. Every later task uses this type for MCP configs.

- [ ] **Step 1: Create the package scaffold**

`Package.swift`:

```swift
// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "MCPEnabler",
    platforms: [.macOS(.v14)],
    targets: [
        .target(name: "MCPEnablerCore"),
        .executableTarget(name: "MCPEnabler", dependencies: ["MCPEnablerCore"]),
        .testTarget(name: "MCPEnablerCoreTests", dependencies: ["MCPEnablerCore"]),
    ]
)
```

`Sources/MCPEnabler/main.swift` (placeholder, replaced in Task 10):

```swift
print("MCP Enabler placeholder — replaced by the app in Task 10")
```

Also append `.sandbox/` to `.gitignore`.

- [ ] **Step 2: Write the failing tests**

`Tests/MCPEnablerCoreTests/JSONValueTests.swift`:

```swift
import XCTest
@testable import MCPEnablerCore

final class JSONValueTests: XCTestCase {
    func testParseAndSerializeRoundTrip() throws {
        let json = #"{"a": 1, "b": "two", "c": [true, null, 2.5], "d": {"e": []}}"#
        let value = try JSONValue.parse(Data(json.utf8))
        XCTAssertEqual(value, .object([
            "a": .int(1),
            "b": .string("two"),
            "c": .array([.bool(true), .null, .double(2.5)]),
            "d": .object(["e": .array([])]),
        ]))
        let reparsed = try JSONValue.parse(try value.serialized())
        XCTAssertEqual(reparsed, value)
    }

    func testIntStaysIntThroughSerialization() throws {
        let data = try JSONValue.object(["n": .int(3)]).serialized()
        XCTAssertTrue(String(decoding: data, as: UTF8.self).contains("\"n\" : 3"))
    }

    func testAnyValueRoundTrip() throws {
        let any: [String: Any] = ["s": "x", "i": 7, "d": 1.5, "b": true,
                                  "n": NSNull(), "a": [1, "y"], "o": ["k": false]]
        let value = try JSONValue(any: any)
        let back = try XCTUnwrap(value.anyValue as? [String: Any])
        let data = try JSONSerialization.data(withJSONObject: back, options: [.sortedKeys])
        let expected = try JSONSerialization.data(
            withJSONObject: any, options: [.sortedKeys])
        XCTAssertEqual(data, expected)
    }

    func testBoolIsNotConfusedWithInt() throws {
        let value = try JSONValue(any: ["t": true, "one": 1])
        XCTAssertEqual(value, .object(["t": .bool(true), "one": .int(1)]))
    }

    func testTypeName() {
        XCTAssertEqual(JSONValue.object([:]).typeName, "object")
        XCTAssertEqual(JSONValue.array([]).typeName, "array")
        XCTAssertEqual(JSONValue.string("").typeName, "string")
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `swift test --filter JSONValueTests`
Expected: compile error — `JSONValue` not defined.

- [ ] **Step 4: Write the implementation**

`Sources/MCPEnablerCore/JSONValue.swift`:

```swift
import Foundation

public enum JSONValueError: Error, Equatable {
    case unsupported(String)
}

public enum JSONValue: Equatable, Hashable {
    case null
    case bool(Bool)
    case int(Int)
    case double(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])
}

extension JSONValue: Codable {
    public init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() { self = .null }
        else if let b = try? c.decode(Bool.self) { self = .bool(b) }
        else if let i = try? c.decode(Int.self) { self = .int(i) }
        else if let d = try? c.decode(Double.self) { self = .double(d) }
        else if let s = try? c.decode(String.self) { self = .string(s) }
        else if let a = try? c.decode([JSONValue].self) { self = .array(a) }
        else if let o = try? c.decode([String: JSONValue].self) { self = .object(o) }
        else {
            throw DecodingError.dataCorruptedError(
                in: c, debugDescription: "Unsupported JSON value")
        }
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch self {
        case .null: try c.encodeNil()
        case .bool(let b): try c.encode(b)
        case .int(let i): try c.encode(i)
        case .double(let d): try c.encode(d)
        case .string(let s): try c.encode(s)
        case .array(let a): try c.encode(a)
        case .object(let o): try c.encode(o)
        }
    }
}

public extension JSONValue {
    static func parse(_ data: Data) throws -> JSONValue {
        try JSONDecoder().decode(JSONValue.self, from: data)
    }

    func serialized() throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return try encoder.encode(self)
    }

    var anyValue: Any {
        switch self {
        case .null: return NSNull()
        case .bool(let b): return b
        case .int(let i): return i
        case .double(let d): return d
        case .string(let s): return s
        case .array(let a): return a.map(\.anyValue)
        case .object(let o): return o.mapValues(\.anyValue)
        }
    }

    init(any: Any) throws {
        switch any {
        case is NSNull:
            self = .null
        case let n as NSNumber:
            if CFGetTypeID(n) == CFBooleanGetTypeID() { self = .bool(n.boolValue) }
            else if CFNumberIsFloatType(n) { self = .double(n.doubleValue) }
            else { self = .int(n.intValue) }
        case let s as String:
            self = .string(s)
        case let a as [Any]:
            self = .array(try a.map(JSONValue.init(any:)))
        case let o as [String: Any]:
            self = .object(try o.mapValues(JSONValue.init(any:)))
        default:
            throw JSONValueError.unsupported(String(describing: type(of: any)))
        }
    }

    var typeName: String {
        switch self {
        case .null: return "null"
        case .bool: return "boolean"
        case .int, .double: return "number"
        case .string: return "string"
        case .array: return "array"
        case .object: return "object"
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `swift test --filter JSONValueTests`
Expected: 5 tests PASS.

- [ ] **Step 6: Commit**

```bash
git add Package.swift Sources Tests .gitignore
git commit -m "feat: package scaffold and JSONValue round-tripping"
```

---

### Task 2: AtomicFile + AppPaths

**Files:**
- Create: `Sources/MCPEnablerCore/AtomicFile.swift`
- Create: `Sources/MCPEnablerCore/AppPaths.swift`
- Test: `Tests/MCPEnablerCoreTests/AtomicFileTests.swift`
- Test: `Tests/MCPEnablerCoreTests/AppPathsTests.swift`

**Interfaces:**
- Consumes: nothing.
- Produces: `enum AtomicFile { static func write(_ data: Data, to url: URL) throws }`; `struct AppPaths { let claudeConfigURL: URL; let storeDirURL: URL; var masterStoreURL: URL; var backupsDirURL: URL; init(claudeConfigURL: URL, storeDirURL: URL); static func live(environment: [String: String] = ProcessInfo.processInfo.environment) -> AppPaths }`. `live()` honors env overrides `MCP_ENABLER_CLAUDE_CONFIG` and `MCP_ENABLER_STORE_DIR`.

- [ ] **Step 1: Write the failing tests**

`Tests/MCPEnablerCoreTests/AtomicFileTests.swift`:

```swift
import XCTest
@testable import MCPEnablerCore

final class AtomicFileTests: XCTestCase {
    var dir: URL!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("atomic-\(UUID().uuidString)")
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    func testWriteCreatesFileAndIntermediateDirectories() throws {
        let url = dir.appendingPathComponent("nested/file.json")
        try AtomicFile.write(Data("hello".utf8), to: url)
        XCTAssertEqual(try String(contentsOf: url, encoding: .utf8), "hello")
    }

    func testWriteReplacesExistingFile() throws {
        let url = dir.appendingPathComponent("file.json")
        try AtomicFile.write(Data("one".utf8), to: url)
        try AtomicFile.write(Data("two".utf8), to: url)
        XCTAssertEqual(try String(contentsOf: url, encoding: .utf8), "two")
    }

    func testNoTempFilesLeftBehind() throws {
        let url = dir.appendingPathComponent("file.json")
        try AtomicFile.write(Data("x".utf8), to: url)
        let names = try FileManager.default.contentsOfDirectory(atPath: dir.path)
        XCTAssertEqual(names, ["file.json"])
    }
}
```

`Tests/MCPEnablerCoreTests/AppPathsTests.swift`:

```swift
import XCTest
@testable import MCPEnablerCore

final class AppPathsTests: XCTestCase {
    func testLiveDefaultsPointAtClaudeAndMCPEnabler() {
        let paths = AppPaths.live(environment: [:])
        XCTAssertTrue(paths.claudeConfigURL.path.hasSuffix(
            "Library/Application Support/Claude/claude_desktop_config.json"))
        XCTAssertTrue(paths.storeDirURL.path.hasSuffix(
            "Library/Application Support/MCP Enabler"))
        XCTAssertEqual(paths.masterStoreURL.lastPathComponent, "mcps.json")
        XCTAssertEqual(paths.backupsDirURL.lastPathComponent, "backups")
    }

    func testEnvironmentOverrides() {
        let paths = AppPaths.live(environment: [
            "MCP_ENABLER_CLAUDE_CONFIG": "/tmp/x/claude.json",
            "MCP_ENABLER_STORE_DIR": "/tmp/x/store",
        ])
        XCTAssertEqual(paths.claudeConfigURL.path, "/tmp/x/claude.json")
        XCTAssertEqual(paths.storeDirURL.path, "/tmp/x/store")
        XCTAssertEqual(paths.masterStoreURL.path, "/tmp/x/store/mcps.json")
        XCTAssertEqual(paths.backupsDirURL.path, "/tmp/x/store/backups")
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `swift test --filter 'AtomicFileTests|AppPathsTests'`
Expected: compile error — `AtomicFile` / `AppPaths` not defined.

- [ ] **Step 3: Write the implementations**

`Sources/MCPEnablerCore/AtomicFile.swift`:

```swift
import Foundation

public enum AtomicFile {
    public static func write(_ data: Data, to url: URL) throws {
        let fm = FileManager.default
        let dir = url.deletingLastPathComponent()
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        let tmp = dir.appendingPathComponent(".\(url.lastPathComponent).tmp-\(UUID().uuidString)")
        try data.write(to: tmp)
        if fm.fileExists(atPath: url.path) {
            _ = try fm.replaceItemAt(url, withItemAt: tmp)
        } else {
            try fm.moveItem(at: tmp, to: url)
        }
    }
}
```

`Sources/MCPEnablerCore/AppPaths.swift`:

```swift
import Foundation

public struct AppPaths {
    public let claudeConfigURL: URL
    public let storeDirURL: URL

    public var masterStoreURL: URL { storeDirURL.appendingPathComponent("mcps.json") }
    public var backupsDirURL: URL { storeDirURL.appendingPathComponent("backups") }

    public init(claudeConfigURL: URL, storeDirURL: URL) {
        self.claudeConfigURL = claudeConfigURL
        self.storeDirURL = storeDirURL
    }

    public static func live(
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> AppPaths {
        let home = FileManager.default.homeDirectoryForCurrentUser
        let appSupport = home.appendingPathComponent("Library/Application Support")
        let claude = environment["MCP_ENABLER_CLAUDE_CONFIG"].map(URL.init(fileURLWithPath:))
            ?? appSupport.appendingPathComponent("Claude/claude_desktop_config.json")
        let store = environment["MCP_ENABLER_STORE_DIR"].map(URL.init(fileURLWithPath:))
            ?? appSupport.appendingPathComponent("MCP Enabler")
        return AppPaths(claudeConfigURL: claude, storeDirURL: store)
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `swift test --filter 'AtomicFileTests|AppPathsTests'`
Expected: 5 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Sources/MCPEnablerCore/AtomicFile.swift Sources/MCPEnablerCore/AppPaths.swift Tests
git commit -m "feat: atomic file writes and path resolution with env overrides"
```

---

### Task 3: Master store models + IO

**Files:**
- Create: `Sources/MCPEnablerCore/MasterStore.swift`
- Test: `Tests/MCPEnablerCoreTests/MasterStoreTests.swift`

**Interfaces:**
- Consumes: `JSONValue`, `AtomicFile`.
- Produces: `enum EditView: String, Codable { case form, json }`; `struct MCPEntry: Equatable, Codable { var enabled: Bool; var config: JSONValue; var lastEditView: EditView; init(enabled: Bool = true, config: JSONValue, lastEditView: EditView = .form) }`; `struct MasterStore: Equatable, Codable { var version: Int; var mcps: [String: MCPEntry]; static let empty: MasterStore }`; `enum MasterStoreIO { static func load(from url: URL, now: Date = Date()) -> (store: MasterStore, corruptFileURL: URL?); static func save(_ store: MasterStore, to url: URL) throws }`.

- [ ] **Step 1: Write the failing tests**

`Tests/MCPEnablerCoreTests/MasterStoreTests.swift`:

```swift
import XCTest
@testable import MCPEnablerCore

final class MasterStoreTests: XCTestCase {
    var dir: URL!
    var url: URL { dir.appendingPathComponent("mcps.json") }

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("store-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    func testLoadMissingFileReturnsEmptyStore() {
        let result = MasterStoreIO.load(from: url)
        XCTAssertEqual(result.store, .empty)
        XCTAssertNil(result.corruptFileURL)
    }

    func testSaveThenLoadRoundTrips() throws {
        var store = MasterStore.empty
        store.mcps["scoutbook"] = MCPEntry(
            enabled: false,
            config: .object(["command": .string("npx"),
                             "args": .array([.string("-y"), .string("mcp-remote"),
                                             .string("https://example.com/mcp")])]),
            lastEditView: .json)
        try MasterStoreIO.save(store, to: url)
        let result = MasterStoreIO.load(from: url)
        XCTAssertEqual(result.store, store)
        XCTAssertNil(result.corruptFileURL)
    }

    func testLoadCorruptFilePreservesItAndReturnsEmpty() throws {
        try Data("{not json!!".utf8).write(to: url)
        let result = MasterStoreIO.load(from: url)
        XCTAssertEqual(result.store, .empty)
        let corrupt = try XCTUnwrap(result.corruptFileURL)
        XCTAssertTrue(corrupt.lastPathComponent.hasPrefix("mcps.corrupt."))
        XCTAssertEqual(try String(contentsOf: corrupt, encoding: .utf8), "{not json!!")
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `swift test --filter MasterStoreTests`
Expected: compile error — types not defined.

- [ ] **Step 3: Write the implementation**

`Sources/MCPEnablerCore/MasterStore.swift`:

```swift
import Foundation

public enum EditView: String, Codable {
    case form, json
}

public struct MCPEntry: Equatable, Codable {
    public var enabled: Bool
    public var config: JSONValue
    public var lastEditView: EditView

    public init(enabled: Bool = true, config: JSONValue, lastEditView: EditView = .form) {
        self.enabled = enabled
        self.config = config
        self.lastEditView = lastEditView
    }
}

public struct MasterStore: Equatable, Codable {
    public var version: Int
    public var mcps: [String: MCPEntry]

    public static let empty = MasterStore(version: 1, mcps: [:])

    public init(version: Int, mcps: [String: MCPEntry]) {
        self.version = version
        self.mcps = mcps
    }
}

public enum MasterStoreIO {
    /// Missing file → empty store. Corrupt file → moved aside to
    /// `mcps.corrupt.<timestamp>.json` (returned) and an empty store; the caller
    /// repopulates it by reconciling against Claude's config.
    public static func load(
        from url: URL, now: Date = Date()
    ) -> (store: MasterStore, corruptFileURL: URL?) {
        let fm = FileManager.default
        guard fm.fileExists(atPath: url.path) else { return (.empty, nil) }
        do {
            let data = try Data(contentsOf: url)
            let store = try JSONDecoder().decode(MasterStore.self, from: data)
            return (store, nil)
        } catch {
            let stamp = BackupTimestamp.string(from: now)
            let aside = url.deletingLastPathComponent()
                .appendingPathComponent("mcps.corrupt.\(stamp).json")
            try? fm.moveItem(at: url, to: aside)
            return (.empty, aside)
        }
    }

    public static func save(_ store: MasterStore, to url: URL) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try AtomicFile.write(try encoder.encode(store), to: url)
    }
}

public enum BackupTimestamp {
    public static func string(from date: Date) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = .current
        f.dateFormat = "yyyy-MM-dd'T'HH-mm-ss-SSS"
        return f.string(from: date)
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `swift test --filter MasterStoreTests`
Expected: 3 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Sources/MCPEnablerCore/MasterStore.swift Tests
git commit -m "feat: master store models with corrupt-file preservation"
```

---

### Task 4: BackupManager

**Files:**
- Create: `Sources/MCPEnablerCore/BackupManager.swift`
- Test: `Tests/MCPEnablerCoreTests/BackupManagerTests.swift`

**Interfaces:**
- Consumes: `BackupTimestamp`.
- Produces: `struct BackupManager { let backupsDir: URL; let keepCount: Int; init(backupsDir: URL, keepCount: Int = 20); func ensureOriginalSnapshot(of url: URL) throws; @discardableResult func backUp(fileAt url: URL, series: String, now: Date = Date()) throws -> URL?; func backups(series: String) throws -> [URL] }`. Series names used app-wide: `"claude_desktop_config"` and `"mcps"`. `backups(series:)` returns newest-first and excludes the `.original` snapshot; `backUp` returns nil if the source file doesn't exist and prunes to `keepCount` after copying.

- [ ] **Step 1: Write the failing tests**

`Tests/MCPEnablerCoreTests/BackupManagerTests.swift`:

```swift
import XCTest
@testable import MCPEnablerCore

final class BackupManagerTests: XCTestCase {
    var dir: URL!
    var source: URL!
    var manager: BackupManager!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("backups-\(UUID().uuidString)")
        source = dir.appendingPathComponent("claude_desktop_config.json")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try Data(#"{"mcpServers": {}}"#.utf8).write(to: source)
        manager = BackupManager(backupsDir: dir.appendingPathComponent("backups"), keepCount: 3)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    func testBackUpCreatesTimestampedCopy() throws {
        let made = try XCTUnwrap(manager.backUp(
            fileAt: source, series: "claude_desktop_config",
            now: Date(timeIntervalSince1970: 1_752_600_000)))
        XCTAssertTrue(made.lastPathComponent.hasPrefix("claude_desktop_config."))
        XCTAssertTrue(made.lastPathComponent.hasSuffix(".json"))
        XCTAssertEqual(try Data(contentsOf: made), try Data(contentsOf: source))
    }

    func testBackUpMissingSourceReturnsNil() throws {
        let missing = dir.appendingPathComponent("nope.json")
        XCTAssertNil(try manager.backUp(fileAt: missing, series: "claude_desktop_config"))
    }

    func testRotationKeepsNewestKeepCount() throws {
        for i in 0..<5 {
            try Data("v\(i)".utf8).write(to: source)
            try manager.backUp(fileAt: source, series: "claude_desktop_config",
                               now: Date(timeIntervalSince1970: Double(1_752_600_000 + i)))
        }
        let kept = try manager.backups(series: "claude_desktop_config")
        XCTAssertEqual(kept.count, 3)
        XCTAssertEqual(try String(contentsOf: kept[0], encoding: .utf8), "v4")
        XCTAssertEqual(try String(contentsOf: kept[2], encoding: .utf8), "v2")
    }

    func testOriginalSnapshotWrittenOnceAndNeverPruned() throws {
        try manager.ensureOriginalSnapshot(of: source)
        try Data("changed".utf8).write(to: source)
        try manager.ensureOriginalSnapshot(of: source)  // second call: no-op
        let original = manager.backupsDir
            .appendingPathComponent("claude_desktop_config.original.json")
        XCTAssertEqual(try String(contentsOf: original, encoding: .utf8),
                       #"{"mcpServers": {}}"#)
        for i in 0..<5 {
            try manager.backUp(fileAt: source, series: "claude_desktop_config",
                               now: Date(timeIntervalSince1970: Double(1_752_700_000 + i)))
        }
        XCTAssertTrue(FileManager.default.fileExists(atPath: original.path))
        XCTAssertFalse(try manager.backups(series: "claude_desktop_config")
            .contains { $0.lastPathComponent.contains(".original.") })
    }

    func testSeriesAreIndependent() throws {
        try manager.backUp(fileAt: source, series: "claude_desktop_config")
        try manager.backUp(fileAt: source, series: "mcps")
        XCTAssertEqual(try manager.backups(series: "claude_desktop_config").count, 1)
        XCTAssertEqual(try manager.backups(series: "mcps").count, 1)
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `swift test --filter BackupManagerTests`
Expected: compile error — `BackupManager` not defined.

- [ ] **Step 3: Write the implementation**

`Sources/MCPEnablerCore/BackupManager.swift`:

```swift
import Foundation

public struct BackupManager {
    public let backupsDir: URL
    public let keepCount: Int

    public init(backupsDir: URL, keepCount: Int = 20) {
        self.backupsDir = backupsDir
        self.keepCount = keepCount
    }

    /// First-run snapshot; written once, never pruned.
    public func ensureOriginalSnapshot(of url: URL) throws {
        let fm = FileManager.default
        guard fm.fileExists(atPath: url.path) else { return }
        let base = url.deletingPathExtension().lastPathComponent
        let dest = backupsDir.appendingPathComponent("\(base).original.json")
        guard !fm.fileExists(atPath: dest.path) else { return }
        try fm.createDirectory(at: backupsDir, withIntermediateDirectories: true)
        try fm.copyItem(at: url, to: dest)
    }

    @discardableResult
    public func backUp(fileAt url: URL, series: String, now: Date = Date()) throws -> URL? {
        let fm = FileManager.default
        guard fm.fileExists(atPath: url.path) else { return nil }
        try fm.createDirectory(at: backupsDir, withIntermediateDirectories: true)
        let dest = backupsDir
            .appendingPathComponent("\(series).\(BackupTimestamp.string(from: now)).json")
        try fm.copyItem(at: url, to: dest)
        try prune(series: series)
        return dest
    }

    /// Timestamped backups for a series, newest first. Excludes `.original`.
    public func backups(series: String) throws -> [URL] {
        let fm = FileManager.default
        guard fm.fileExists(atPath: backupsDir.path) else { return [] }
        return try fm.contentsOfDirectory(at: backupsDir, includingPropertiesForKeys: nil)
            .filter {
                $0.lastPathComponent.hasPrefix("\(series).")
                    && !$0.lastPathComponent.contains(".original.")
            }
            .sorted { $0.lastPathComponent > $1.lastPathComponent }
    }

    private func prune(series: String) throws {
        let all = try backups(series: series)
        for stale in all.dropFirst(keepCount) {
            try FileManager.default.removeItem(at: stale)
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `swift test --filter BackupManagerTests`
Expected: 5 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Sources/MCPEnablerCore/BackupManager.swift Tests
git commit -m "feat: timestamped backups with rotation and permanent original snapshot"
```

---

### Task 5: ClaudeConfigIO — read and key-preserving write

**Files:**
- Create: `Sources/MCPEnablerCore/ClaudeConfigIO.swift`
- Create: `Tests/MCPEnablerCoreTests/Fixtures.swift`
- Test: `Tests/MCPEnablerCoreTests/ClaudeConfigIOTests.swift`

**Interfaces:**
- Consumes: `JSONValue`, `AtomicFile`.
- Produces: `enum ClaudeConfigError: Error, Equatable { case malformed(String) }`; `enum ClaudeConfigIO { static func readMCPServers(at url: URL) throws -> [String: JSONValue]; static func write(mcpServers: [String: JSONValue], to url: URL) throws }`. Read: missing file or absent key → `[:]`; unparseable file or non-object `mcpServers` → `.malformed`. Write: re-reads the file fresh, replaces only `mcpServers`, preserves all other keys by value, atomic; missing file → created; malformed file → throws (never overwritten).
- Also produces test helper `Fixtures.realisticClaudeConfig` used by later tasks.

- [ ] **Step 1: Write the fixture**

`Tests/MCPEnablerCoreTests/Fixtures.swift` (mirrors the real config shape: three mcp-remote servers plus non-MCP keys):

```swift
import Foundation

enum Fixtures {
    static let realisticClaudeConfig = """
    {
      "mcpServers": {
        "scoutbook": {
          "command": "npx",
          "args": ["-y", "mcp-remote", "https://scoutbook.example.com/mcp"]
        },
        "aws-mcp": {
          "command": "npx",
          "args": ["-y", "mcp-remote", "https://aws-mcp.us-east-1.api.aws/mcp"]
        },
        "service-now": {
          "command": "npx",
          "args": ["-y", "mcp-remote", "https://snow.example.com/mcp"]
        }
      },
      "coworkUserFilesPath": "/Users/someone/Documents/Claude",
      "preferences": {
        "coworkScheduledTasksEnabled": true,
        "sidebarMode": "epitaxy",
        "bypassPermissionsGateByAccount": { "024145b7": true },
        "epitaxyPrefs": { "rowSplit": 0.5, "draftNonce": 0 }
      },
      "someFutureKey": [1, 2, {"nested": null}]
    }
    """
}
```

- [ ] **Step 2: Write the failing tests**

`Tests/MCPEnablerCoreTests/ClaudeConfigIOTests.swift`:

```swift
import XCTest
@testable import MCPEnablerCore

final class ClaudeConfigIOTests: XCTestCase {
    var dir: URL!
    var url: URL { dir.appendingPathComponent("claude_desktop_config.json") }

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("claude-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
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
        XCTAssertEqual(servers["scoutbook"], .object([
            "command": .string("npx"),
            "args": .array([.string("-y"), .string("mcp-remote"),
                            .string("https://scoutbook.example.com/mcp")]),
        ]))
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
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `swift test --filter ClaudeConfigIOTests`
Expected: compile error — `ClaudeConfigIO` not defined.

- [ ] **Step 4: Write the implementation**

`Sources/MCPEnablerCore/ClaudeConfigIO.swift`:

```swift
import Foundation

public enum ClaudeConfigError: Error, Equatable {
    case malformed(String)
}

public enum ClaudeConfigIO {
    public static func readMCPServers(at url: URL) throws -> [String: JSONValue] {
        guard let root = try readRootIfPresent(at: url) else { return [:] }
        guard let raw = root["mcpServers"] else { return [:] }
        guard let dict = raw as? [String: Any] else {
            throw ClaudeConfigError.malformed("mcpServers is not a JSON object")
        }
        return try dict.mapValues(JSONValue.init(any:))
    }

    /// Reads the file fresh, replaces ONLY the mcpServers key, preserves every
    /// other key by value, and writes atomically. Missing file → created.
    /// Malformed file → throws; the file is never overwritten blindly.
    public static func write(mcpServers: [String: JSONValue], to url: URL) throws {
        var root = try readRootIfPresent(at: url) ?? [:]
        root["mcpServers"] = mcpServers.mapValues(\.anyValue)
        let data = try JSONSerialization.data(
            withJSONObject: root, options: [.prettyPrinted, .sortedKeys])
        try AtomicFile.write(data, to: url)
    }

    private static func readRootIfPresent(at url: URL) throws -> [String: Any]? {
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        let data = try Data(contentsOf: url)
        guard !data.isEmpty else { return [:] }
        let parsed: Any
        do {
            parsed = try JSONSerialization.jsonObject(with: data)
        } catch {
            throw ClaudeConfigError.malformed(error.localizedDescription)
        }
        guard let root = parsed as? [String: Any] else {
            throw ClaudeConfigError.malformed("top level is not a JSON object")
        }
        return root
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `swift test --filter ClaudeConfigIOTests`
Expected: 9 tests PASS.

- [ ] **Step 6: Commit**

```bash
git add Sources/MCPEnablerCore/ClaudeConfigIO.swift Tests
git commit -m "feat: Claude config read/write preserving all non-mcpServers keys"
```

---

### Task 6: Remote-pattern detection

**Files:**
- Create: `Sources/MCPEnablerCore/RemotePattern.swift`
- Test: `Tests/MCPEnablerCoreTests/RemotePatternTests.swift`

**Interfaces:**
- Consumes: `JSONValue`.
- Produces: `enum RemotePattern { static func detect(_ config: JSONValue) -> String?; static func make(url: String) -> JSONValue }`. `detect` returns the server URL iff `command == "npx"` and `args` is exactly `["-y", "mcp-remote", <http(s) url>]` or `["mcp-remote", <http(s) url>]` (other keys such as `env`/`headers` don't disqualify — they surface as Additional fields). `make(url:)` builds `{"command":"npx","args":["-y","mcp-remote",url]}`.

- [ ] **Step 1: Write the failing tests**

`Tests/MCPEnablerCoreTests/RemotePatternTests.swift`:

```swift
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `swift test --filter RemotePatternTests`
Expected: compile error — `RemotePattern` not defined.

- [ ] **Step 3: Write the implementation**

`Sources/MCPEnablerCore/RemotePattern.swift`:

```swift
import Foundation

/// Recognizes the `npx [-y] mcp-remote <url>` bridge pattern so the form view can
/// show just Name + Server URL. Keys other than command/args (env, headers, …)
/// don't disqualify — they surface in the form's read-only Additional fields.
public enum RemotePattern {
    public static func detect(_ config: JSONValue) -> String? {
        guard case .object(let object) = config,
              case .string("npx") = object["command"] ?? .null,
              case .array(let rawArgs) = object["args"] ?? .null
        else { return nil }
        var args: [String] = []
        for raw in rawArgs {
            guard case .string(let s) = raw else { return nil }
            args.append(s)
        }
        if args.first == "-y" { args.removeFirst() }
        guard args.count == 2, args[0] == "mcp-remote" else { return nil }
        guard let url = URL(string: args[1]), let scheme = url.scheme?.lowercased(),
              scheme == "http" || scheme == "https", url.host != nil
        else { return nil }
        return args[1]
    }

    public static func make(url: String) -> JSONValue {
        .object(["command": .string("npx"),
                 "args": .array([.string("-y"), .string("mcp-remote"), .string(url)])])
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `swift test --filter RemotePatternTests`
Expected: 9 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Sources/MCPEnablerCore/RemotePattern.swift Tests
git commit -m "feat: mcp-remote pattern detection and template"
```

---

### Task 7: Form representability analysis

**Files:**
- Create: `Sources/MCPEnablerCore/FormMapper.swift`
- Test: `Tests/MCPEnablerCoreTests/FormMapperTests.swift`

**Interfaces:**
- Consumes: `JSONValue`.
- Produces: `struct FormModel: Equatable { var command: String; var args: [String]; var env: [String: String]; var additional: [String: JSONValue] }`; `struct FormAnalysis: Equatable { var model: FormModel; var lost: [String]; var isLossless: Bool }`; `enum FormMapper { static func analyze(_ config: JSONValue) -> FormAnalysis; static func serialize(_ model: FormModel) -> JSONValue }`. `lost` holds human-readable descriptions (e.g. `"args[3] (object)"`) of elements the form cannot represent — the exact strings shown in the lossy-toggle warning dialog. Unknown top-level keys are NOT lost; they land in `additional` and round-trip through `serialize`.

- [ ] **Step 1: Write the failing tests**

`Tests/MCPEnablerCoreTests/FormMapperTests.swift`:

```swift
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
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `swift test --filter FormMapperTests`
Expected: compile error — types not defined.

- [ ] **Step 3: Write the implementation**

`Sources/MCPEnablerCore/FormMapper.swift`:

```swift
import Foundation

public struct FormModel: Equatable {
    public var command: String
    public var args: [String]
    public var env: [String: String]
    /// Keys the form has no widget for — preserved verbatim, shown read-only.
    public var additional: [String: JSONValue]

    public init(command: String = "", args: [String] = [],
                env: [String: String] = [:], additional: [String: JSONValue] = [:]) {
        self.command = command
        self.args = args
        self.env = env
        self.additional = additional
    }
}

public struct FormAnalysis: Equatable {
    public var model: FormModel
    /// Human-readable descriptions of elements the form CANNOT represent.
    /// Empty means switching JSON → Form loses nothing.
    public var lost: [String]
    public var isLossless: Bool { lost.isEmpty }
}

public enum FormMapper {
    private static let formKeys: Set<String> = ["command", "args", "env"]

    public static func analyze(_ config: JSONValue) -> FormAnalysis {
        guard case .object(let object) = config else {
            return FormAnalysis(model: FormModel(),
                                lost: ["entire configuration (not a JSON object)"])
        }
        var model = FormModel()
        var lost: [String] = []

        switch object["command"] {
        case .string(let s): model.command = s
        case .none: break
        case .some(let other): lost.append("command (\(other.typeName))")
        }

        switch object["args"] {
        case .array(let items):
            for (index, item) in items.enumerated() {
                if case .string(let s) = item { model.args.append(s) }
                else { lost.append("args[\(index)] (\(item.typeName))") }
            }
        case .none: break
        case .some: lost.append("args (not an array)")
        }

        switch object["env"] {
        case .object(let pairs):
            for (key, value) in pairs {
                if case .string(let s) = value { model.env[key] = s }
                else { lost.append("env.\(key) (\(value.typeName))") }
            }
        case .none: break
        case .some: lost.append("env (not an object)")
        }

        model.additional = object.filter { !formKeys.contains($0.key) }
        return FormAnalysis(model: model, lost: lost.sorted())
    }

    public static func serialize(_ model: FormModel) -> JSONValue {
        var object = model.additional
        object["command"] = .string(model.command)
        if !model.args.isEmpty {
            object["args"] = .array(model.args.map(JSONValue.string))
        }
        if !model.env.isEmpty {
            object["env"] = .object(model.env.mapValues(JSONValue.string))
        }
        return .object(object)
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `swift test --filter FormMapperTests`
Expected: 7 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Sources/MCPEnablerCore/FormMapper.swift Tests
git commit -m "feat: form representability analysis with additional-fields preservation"
```

---

### Task 8: Reconciler

**Files:**
- Create: `Sources/MCPEnablerCore/Reconciler.swift`
- Test: `Tests/MCPEnablerCoreTests/ReconcilerTests.swift`

**Interfaces:**
- Consumes: `MasterStore`, `MCPEntry`, `JSONValue`.
- Produces: `struct ReconcileOutcome: Equatable { var store: MasterStore; var missingEnabled: [String]; var storeChanged: Bool }`; `enum Reconciler { static func reconcile(store: MasterStore, claudeServers: [String: JSONValue]) -> ReconcileOutcome }`. Pure function implementing the spec's four rules: unknown-in-file → import enabled; present-in-both-differs → file wins; disabled-but-present → mark enabled; enabled-but-missing → flag in `missingEnabled` (sorted), never delete.

- [ ] **Step 1: Write the failing tests**

`Tests/MCPEnablerCoreTests/ReconcilerTests.swift`:

```swift
import XCTest
@testable import MCPEnablerCore

final class ReconcilerTests: XCTestCase {
    private let configA = JSONValue.object(["command": .string("a")])
    private let configB = JSONValue.object(["command": .string("b")])

    private func store(_ mcps: [String: MCPEntry]) -> MasterStore {
        MasterStore(version: 1, mcps: mcps)
    }

    func testUnknownServerIsImportedEnabled() {
        let outcome = Reconciler.reconcile(store: .empty, claudeServers: ["new": configA])
        XCTAssertEqual(outcome.store.mcps["new"],
                       MCPEntry(enabled: true, config: configA, lastEditView: .form))
        XCTAssertTrue(outcome.storeChanged)
        XCTAssertEqual(outcome.missingEnabled, [])
    }

    func testExternalEditWinsOverStore() {
        let outcome = Reconciler.reconcile(
            store: store(["s": MCPEntry(enabled: true, config: configA, lastEditView: .json)]),
            claudeServers: ["s": configB])
        XCTAssertEqual(outcome.store.mcps["s"]?.config, configB)
        XCTAssertEqual(outcome.store.mcps["s"]?.lastEditView, .json,
                       "view memory must survive reconciliation")
        XCTAssertTrue(outcome.storeChanged)
    }

    func testDisabledButPresentBecomesEnabled() {
        let outcome = Reconciler.reconcile(
            store: store(["s": MCPEntry(enabled: false, config: configA)]),
            claudeServers: ["s": configA])
        XCTAssertEqual(outcome.store.mcps["s"]?.enabled, true)
        XCTAssertTrue(outcome.storeChanged)
    }

    func testEnabledButMissingIsFlaggedNotDeleted() {
        let outcome = Reconciler.reconcile(
            store: store(["gone": MCPEntry(enabled: true, config: configA),
                          "also-gone": MCPEntry(enabled: true, config: configB)]),
            claudeServers: [:])
        XCTAssertEqual(outcome.missingEnabled, ["also-gone", "gone"])
        XCTAssertEqual(outcome.store.mcps.count, 2, "never silently deleted")
        XCTAssertFalse(outcome.storeChanged)
    }

    func testDisabledAndAbsentIsNormalNoChange() {
        let s = store(["off": MCPEntry(enabled: false, config: configA)])
        let outcome = Reconciler.reconcile(store: s, claudeServers: [:])
        XCTAssertEqual(outcome.store, s)
        XCTAssertFalse(outcome.storeChanged)
        XCTAssertEqual(outcome.missingEnabled, [])
    }

    func testIdenticalStateIsNoChange() {
        let s = store(["s": MCPEntry(enabled: true, config: configA)])
        let outcome = Reconciler.reconcile(store: s, claudeServers: ["s": configA])
        XCTAssertEqual(outcome.store, s)
        XCTAssertFalse(outcome.storeChanged)
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `swift test --filter ReconcilerTests`
Expected: compile error — `Reconciler` not defined.

- [ ] **Step 3: Write the implementation**

`Sources/MCPEnablerCore/Reconciler.swift`:

```swift
import Foundation

public struct ReconcileOutcome: Equatable {
    public var store: MasterStore
    /// Names enabled in the store but absent from Claude's file — the
    /// "Claude wiped my config" recovery flag. Sorted for stable display.
    public var missingEnabled: [String]
    public var storeChanged: Bool
}

public enum Reconciler {
    public static func reconcile(
        store: MasterStore, claudeServers: [String: JSONValue]
    ) -> ReconcileOutcome {
        var result = store
        var changed = false

        for (name, config) in claudeServers {
            if var entry = result.mcps[name] {
                if entry.config != config { entry.config = config; changed = true }
                if !entry.enabled { entry.enabled = true; changed = true }
                result.mcps[name] = entry
            } else {
                result.mcps[name] = MCPEntry(enabled: true, config: config)
                changed = true
            }
        }

        let missing = store.mcps
            .filter { $0.value.enabled && claudeServers[$0.key] == nil }
            .keys.sorted()

        return ReconcileOutcome(store: result, missingEnabled: missing,
                                storeChanged: changed)
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `swift test --filter ReconcilerTests`
Expected: 6 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Sources/MCPEnablerCore/Reconciler.swift Tests
git commit -m "feat: reconciliation of master store against Claude's config"
```

---

### Task 9: ConfigService — backed-up apply/save/restore orchestration

**Files:**
- Create: `Sources/MCPEnablerCore/ConfigService.swift`
- Test: `Tests/MCPEnablerCoreTests/ConfigServiceTests.swift`

**Interfaces:**
- Consumes: everything from Tasks 2–8.
- Produces: `struct ConfigService { let paths: AppPaths; let backups: BackupManager; init(paths: AppPaths); func loadAndReconcile() throws -> (store: MasterStore, missingEnabled: [String], notes: [String]); func saveStore(_ store: MasterStore) throws; func apply(_ store: MasterStore) throws; func restoreClaudeConfig(from backup: URL, mergedWith store: MasterStore) throws }`. `init(paths:)` builds `BackupManager(backupsDir: paths.backupsDirURL)`. `apply` = original snapshot + backup + write enabled subset. `saveStore` = backup `mcps.json` + atomic save. `loadAndReconcile` = load store (handling corruption with a note), read Claude servers, reconcile, persist if changed. `restoreClaudeConfig` = backup current file, copy backup over it, then reconcile.

- [ ] **Step 1: Write the failing tests**

`Tests/MCPEnablerCoreTests/ConfigServiceTests.swift`:

```swift
import XCTest
@testable import MCPEnablerCore

final class ConfigServiceTests: XCTestCase {
    var dir: URL!
    var paths: AppPaths!
    var service: ConfigService!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("svc-\(UUID().uuidString)")
        let claudeDir = dir.appendingPathComponent("Claude")
        try FileManager.default.createDirectory(at: claudeDir, withIntermediateDirectories: true)
        paths = AppPaths(
            claudeConfigURL: claudeDir.appendingPathComponent("claude_desktop_config.json"),
            storeDirURL: dir.appendingPathComponent("MCP Enabler"))
        try Data(Fixtures.realisticClaudeConfig.utf8).write(to: paths.claudeConfigURL)
        service = ConfigService(paths: paths)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    func testFirstLoadImportsAllServersEnabled() throws {
        let result = try service.loadAndReconcile()
        XCTAssertEqual(Set(result.store.mcps.keys),
                       ["scoutbook", "aws-mcp", "service-now"])
        XCTAssertTrue(result.store.mcps.values.allSatisfy(\.enabled))
        XCTAssertEqual(result.missingEnabled, [])
        // reconciled store was persisted
        XCTAssertEqual(MasterStoreIO.load(from: paths.masterStoreURL).store, result.store)
    }

    func testApplyWritesEnabledSubsetWithBackups() throws {
        var store = try service.loadAndReconcile().store
        store.mcps["aws-mcp"]?.enabled = false
        try service.apply(store)
        XCTAssertEqual(Set(try ClaudeConfigIO.readMCPServers(at: paths.claudeConfigURL).keys),
                       ["scoutbook", "service-now"])
        // non-MCP keys survived
        let root = try XCTUnwrap(JSONSerialization.jsonObject(
            with: Data(contentsOf: paths.claudeConfigURL)) as? [String: Any])
        XCTAssertNotNil(root["preferences"])
        XCTAssertNotNil(root["someFutureKey"])
        // backups exist: original + timestamped
        let backups = service.backups
        XCTAssertEqual(try backups.backups(series: "claude_desktop_config").count, 1)
        XCTAssertTrue(FileManager.default.fileExists(atPath: backups.backupsDir
            .appendingPathComponent("claude_desktop_config.original.json").path))
    }

    func testSaveStoreBacksUpPreviousVersion() throws {
        let store = try service.loadAndReconcile().store
        try service.saveStore(store)  // first explicit save; store file already exists
        XCTAssertEqual(try service.backups.backups(series: "mcps").count, 1)
    }

    func testWipeRecoveryFlow() throws {
        let store = try service.loadAndReconcile().store
        // Claude wipes the file to a preferences-only stub (issue #32345 shape)
        try Data(#"{"preferences": {}}"#.utf8).write(to: paths.claudeConfigURL)
        let result = try service.loadAndReconcile()
        XCTAssertEqual(result.missingEnabled, ["aws-mcp", "scoutbook", "service-now"])
        XCTAssertEqual(result.store.mcps.count, 3, "nothing deleted")
        // restore: apply the store puts them back, preserving the stub's keys
        try service.apply(store)
        XCTAssertEqual(try ClaudeConfigIO.readMCPServers(at: paths.claudeConfigURL).count, 3)
    }

    func testCorruptMasterStoreIsRebuiltWithNote() throws {
        _ = try service.loadAndReconcile()
        try Data("garbage".utf8).write(to: paths.masterStoreURL)
        let result = try service.loadAndReconcile()
        XCTAssertEqual(result.store.mcps.count, 3, "rebuilt from Claude's config")
        XCTAssertEqual(result.notes.count, 1)
        XCTAssertTrue(result.notes[0].contains("mcps.corrupt."))
    }

    func testRestoreClaudeConfigFromBackup() throws {
        var store = try service.loadAndReconcile().store
        store.mcps["aws-mcp"]?.enabled = false
        try service.apply(store)  // creates a backup of the 3-server file
        let backup = try XCTUnwrap(
            try service.backups.backups(series: "claude_desktop_config").first)
        try service.restoreClaudeConfig(from: backup, mergedWith: store)
        XCTAssertEqual(try ClaudeConfigIO.readMCPServers(at: paths.claudeConfigURL).count, 3)
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `swift test --filter ConfigServiceTests`
Expected: compile error — `ConfigService` not defined.

- [ ] **Step 3: Write the implementation**

`Sources/MCPEnablerCore/ConfigService.swift`:

```swift
import Foundation

/// Orchestrates every stateful operation, guaranteeing the backup-before-write
/// invariant. The UI layer calls only this type for file operations.
public struct ConfigService {
    public let paths: AppPaths
    public let backups: BackupManager

    public init(paths: AppPaths) {
        self.paths = paths
        self.backups = BackupManager(backupsDir: paths.backupsDirURL)
    }

    /// Load master store (handling corruption), read Claude's servers,
    /// reconcile, persist the store if reconciliation changed it.
    public func loadAndReconcile() throws
        -> (store: MasterStore, missingEnabled: [String], notes: [String]) {
        var notes: [String] = []
        let loaded = MasterStoreIO.load(from: paths.masterStoreURL)
        if let corrupt = loaded.corruptFileURL {
            notes.append(
                "The MCP list file was unreadable; it was preserved as "
                + "\(corrupt.lastPathComponent) and rebuilt from Claude's config.")
        }
        let servers = try ClaudeConfigIO.readMCPServers(at: paths.claudeConfigURL)
        let outcome = Reconciler.reconcile(store: loaded.store, claudeServers: servers)
        if outcome.storeChanged || loaded.corruptFileURL != nil {
            try saveStore(outcome.store)
        }
        return (outcome.store, outcome.missingEnabled, notes)
    }

    /// Backup mcps.json (if present), then atomically save the store.
    public func saveStore(_ store: MasterStore) throws {
        try backups.backUp(fileAt: paths.masterStoreURL, series: "mcps")
        try MasterStoreIO.save(store, to: paths.masterStoreURL)
    }

    /// Snapshot original (first run), backup Claude's config, then write the
    /// enabled subset into it, preserving all other keys.
    public func apply(_ store: MasterStore) throws {
        try backups.ensureOriginalSnapshot(of: paths.claudeConfigURL)
        try backups.backUp(fileAt: paths.claudeConfigURL, series: "claude_desktop_config")
        let enabled = store.mcps.filter(\.value.enabled).mapValues(\.config)
        try ClaudeConfigIO.write(mcpServers: enabled, to: paths.claudeConfigURL)
    }

    /// Backup the current file, copy the chosen backup over it, then persist a
    /// freshly reconciled store so the UI reflects the restored contents.
    public func restoreClaudeConfig(from backup: URL, mergedWith store: MasterStore) throws {
        try backups.backUp(fileAt: paths.claudeConfigURL, series: "claude_desktop_config")
        let data = try Data(contentsOf: backup)
        try AtomicFile.write(data, to: paths.claudeConfigURL)
        let servers = try ClaudeConfigIO.readMCPServers(at: paths.claudeConfigURL)
        let outcome = Reconciler.reconcile(store: store, claudeServers: servers)
        if outcome.storeChanged { try saveStore(outcome.store) }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `swift test --filter ConfigServiceTests`
Expected: 6 tests PASS.

- [ ] **Step 5: Run the FULL suite and commit**

Run: `swift test`
Expected: all tests PASS.

```bash
git add Sources/MCPEnablerCore/ConfigService.swift Tests
git commit -m "feat: ConfigService orchestrating backed-up apply/save/restore"
```

---

### Task 10: App scaffold — AppState + MenuBarExtra + popover with toggles and Apply

**Files:**
- Delete: `Sources/MCPEnabler/main.swift`
- Create: `Sources/MCPEnabler/MCPEnablerApp.swift`
- Create: `Sources/MCPEnabler/AppState.swift`
- Create: `Sources/MCPEnabler/PopoverView.swift`

**Interfaces:**
- Consumes: `ConfigService`, `MasterStore`, `MCPEntry`, `RemotePattern`, `Reconciler` outputs.
- Produces: `@MainActor final class AppState: ObservableObject` with `@Published var store: MasterStore`, `@Published var missingEnabled: [String]`, `@Published var lastError: String?`, `@Published var showRestartPrompt: Bool`, `var isDirty: Bool`, and methods `reload()`, `setEnabled(_ name: String, _ on: Bool)`, `apply()`, `upsert(name: String, entry: MCPEntry, renamedFrom: String?) -> String?` (returns validation error or nil), `remove(name: String)`, `restoreMissing()`, `markMissingDisabled()`. Tasks 11–13 add UI over these exact names.

- [ ] **Step 1: Write AppState**

`Sources/MCPEnabler/AppState.swift`:

```swift
import Foundation
import MCPEnablerCore

@MainActor
final class AppState: ObservableObject {
    @Published var store: MasterStore = .empty
    @Published var missingEnabled: [String] = []
    @Published var lastError: String?
    @Published var showRestartPrompt = false
    /// mcpServers as last read from / written to Claude's file, for dirty tracking.
    @Published private(set) var appliedServers: [String: JSONValue] = [:]

    let service: ConfigService

    init(service: ConfigService = ConfigService(paths: .live())) {
        self.service = service
        reload()
    }

    var isDirty: Bool {
        store.mcps.filter(\.value.enabled).mapValues(\.config) != appliedServers
    }

    var sortedNames: [String] { store.mcps.keys.sorted() }

    func reload() {
        do {
            let result = try service.loadAndReconcile()
            store = result.store
            missingEnabled = result.missingEnabled
            appliedServers = try ClaudeConfigIO.readMCPServers(
                at: service.paths.claudeConfigURL)
            if let note = result.notes.first { lastError = note }
        } catch {
            lastError = friendly(error)
        }
    }

    func setEnabled(_ name: String, _ on: Bool) {
        store.mcps[name]?.enabled = on
        persistStore()
    }

    func apply() {
        do {
            try service.apply(store)
            appliedServers = store.mcps.filter(\.value.enabled).mapValues(\.config)
            missingEnabled = []
            showRestartPrompt = true
            lastError = nil
        } catch {
            lastError = friendly(error)
        }
    }

    /// Validates and saves an entry. Returns an error message, or nil on success.
    func upsert(name: String, entry: MCPEntry, renamedFrom oldName: String?) -> String? {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return "Name must not be empty." }
        if trimmed != oldName, store.mcps[trimmed] != nil {
            return "An MCP named “\(trimmed)” already exists."
        }
        if let old = oldName, old != trimmed { store.mcps.removeValue(forKey: old) }
        store.mcps[trimmed] = entry
        persistStore()
        return nil
    }

    func remove(name: String) {
        store.mcps.removeValue(forKey: name)
        persistStore()
    }

    /// Recovery for externally wiped MCPs: rewrite Claude's config from the store.
    func restoreMissing() { apply() }

    func markMissingDisabled() {
        for name in missingEnabled { store.mcps[name]?.enabled = false }
        missingEnabled = []
        persistStore()
    }

    private func persistStore() {
        do { try service.saveStore(store) } catch { lastError = friendly(error) }
    }

    private func friendly(_ error: Error) -> String {
        if case ClaudeConfigError.malformed(let detail) = error {
            return "Claude's config file is not valid JSON (\(detail)). "
                + "Nothing was written. Use Backups ▸ Restore… to recover it."
        }
        return error.localizedDescription
    }
}
```

- [ ] **Step 2: Write the app entry point and popover**

`Sources/MCPEnabler/MCPEnablerApp.swift`:

```swift
import SwiftUI
import MCPEnablerCore

@main
struct MCPEnablerApp: App {
    @StateObject private var state = AppState()

    var body: some Scene {
        MenuBarExtra {
            PopoverView()
                .environmentObject(state)
        } label: {
            Image(systemName: state.missingEnabled.isEmpty
                ? "switch.2" : "exclamationmark.triangle.fill")
        }
        .menuBarExtraStyle(.window)
    }
}
```

`Sources/MCPEnabler/PopoverView.swift`:

```swift
import SwiftUI
import MCPEnablerCore

struct PopoverView: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if !state.missingEnabled.isEmpty { missingBanner }
            if let error = state.lastError { errorBanner(error) }
            mcpList
            Divider()
            footer
        }
        .frame(width: 380)
        .onAppear { state.reload() }
    }

    private var missingBanner: some View {
        VStack(alignment: .leading, spacing: 6) {
            Label("Claude's config is missing \(state.missingEnabled.count) MCP(s): "
                  + state.missingEnabled.joined(separator: ", "),
                  systemImage: "exclamationmark.triangle.fill")
                .font(.callout)
            HStack {
                Button("Restore") { state.restoreMissing() }
                Button("Mark Disabled") { state.markMissingDisabled() }
            }
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.yellow.opacity(0.15))
    }

    private func errorBanner(_ message: String) -> some View {
        Label(message, systemImage: "xmark.octagon.fill")
            .font(.callout)
            .foregroundStyle(.red)
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var mcpList: some View {
        ScrollView {
            VStack(spacing: 0) {
                ForEach(state.sortedNames, id: \.self) { name in
                    MCPRow(name: name)
                    Divider()
                }
                if state.store.mcps.isEmpty {
                    Text("No MCPs configured yet — add one below.")
                        .foregroundStyle(.secondary)
                        .padding()
                }
            }
        }
        .frame(maxHeight: 320)
    }

    private var footer: some View {
        HStack {
            // Add / Backups menus arrive in Tasks 12–13.
            Spacer()
            if state.showRestartPrompt {
                Button("Restart Claude") { /* wired in Task 13 */ }
                Button("Later") { state.showRestartPrompt = false }
            } else if state.isDirty {
                Button("Apply") { state.apply() }
                    .keyboardShortcut(.defaultAction)
            }
            Button("Quit") { NSApp.terminate(nil) }
        }
        .padding(10)
    }
}

struct MCPRow: View {
    @EnvironmentObject var state: AppState
    let name: String

    var body: some View {
        HStack(spacing: 10) {
            Toggle("", isOn: Binding(
                get: { state.store.mcps[name]?.enabled ?? false },
                set: { state.setEnabled(name, $0) }))
                .toggleStyle(.switch)
                .controlSize(.small)
                .labelsHidden()
            Text(name).fontWeight(.medium)
            Spacer()
            if let config = state.store.mcps[name]?.config,
               RemotePattern.detect(config) != nil {
                Text("REMOTE").font(.caption2).foregroundStyle(.secondary)
                    .padding(.horizontal, 5).padding(.vertical, 1)
                    .overlay(RoundedRectangle(cornerRadius: 4)
                        .stroke(.secondary.opacity(0.4)))
            } else {
                Text("LOCAL").font(.caption2).foregroundStyle(.secondary)
                    .padding(.horizontal, 5).padding(.vertical, 1)
                    .overlay(RoundedRectangle(cornerRadius: 4)
                        .stroke(.secondary.opacity(0.4)))
            }
            // Edit chevron arrives in Task 11.
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 7)
    }
}
```

- [ ] **Step 3: Build and set up the sandbox**

Run: `swift build`
Expected: builds with no errors (warnings acceptable).

```bash
mkdir -p .sandbox/store
cp "$HOME/Library/Application Support/Claude/claude_desktop_config.json" .sandbox/claude_desktop_config.json
```

- [ ] **Step 4: Manual verification (sandboxed — never the real config)**

```bash
MCP_ENABLER_CLAUDE_CONFIG="$PWD/.sandbox/claude_desktop_config.json" \
MCP_ENABLER_STORE_DIR="$PWD/.sandbox/store" \
swift run MCPEnabler
```

Verify: a switch icon appears in the menu bar; clicking shows three MCPs (scoutbook, aws-mcp, service-now) toggled on with REMOTE badges; toggling one off shows Apply; Apply removes it from `.sandbox/claude_desktop_config.json` `mcpServers` while `preferences` survives; `.sandbox/store/backups/` contains `claude_desktop_config.original.json` and a timestamped backup; deleting an entry from the sandbox config by hand and reopening the popover shows the yellow missing banner, and Restore puts it back. Quit the app.

- [ ] **Step 5: Run full test suite, then commit**

Run: `swift test`
Expected: all PASS.

```bash
git rm Sources/MCPEnabler/main.swift
git add Sources/MCPEnabler
git commit -m "feat: menu bar app with toggles, apply, and missing-MCP recovery banner"
```

---

### Task 11: Edit sheet — Form/JSON toggle with view memory and lossy warning

**Files:**
- Create: `Sources/MCPEnabler/EditSheetView.swift`
- Modify: `Sources/MCPEnabler/PopoverView.swift` (row chevron + sheet presentation)

**Interfaces:**
- Consumes: `FormMapper`, `RemotePattern`, `EditView`, `AppState.upsert/remove`.
- Produces: `struct EditTarget: Identifiable { let id: String; var name: String; var entry: MCPEntry; var isNew: Bool }` and `struct EditSheetView: View { let target: EditTarget }`. Task 12 reuses `EditTarget`/`EditSheetView` for the Add flows.

- [ ] **Step 1: Write the edit sheet**

`Sources/MCPEnabler/EditSheetView.swift`:

```swift
import SwiftUI
import MCPEnablerCore

struct EditTarget: Identifiable {
    let id: String          // UUID for new, name for existing
    var name: String
    var entry: MCPEntry
    var isNew: Bool

    static func existing(name: String, entry: MCPEntry) -> EditTarget {
        EditTarget(id: name, name: name, entry: entry, isNew: false)
    }

    static func new(template: JSONValue) -> EditTarget {
        EditTarget(id: UUID().uuidString, name: "",
                   entry: MCPEntry(config: template), isNew: true)
    }
}

struct EditSheetView: View {
    @EnvironmentObject var state: AppState
    @Environment(\.dismiss) private var dismiss
    let target: EditTarget

    @State private var view: EditView
    @State private var name: String
    @State private var remoteURL: String        // non-nil pattern → remote form
    @State private var isRemote: Bool
    @State private var form: FormModel
    @State private var jsonText: String
    @State private var jsonError: String?
    @State private var lossWarning: [String]?   // non-nil → confirmation shown
    @State private var validationError: String?
    @State private var confirmRemove = false
    @State private var envRevealed: Set<String> = []

    init(target: EditTarget) {
        self.target = target
        _name = State(initialValue: target.name)
        _view = State(initialValue: target.entry.lastEditView)
        let detected = RemotePattern.detect(target.entry.config)
        _isRemote = State(initialValue: detected != nil)
        _remoteURL = State(initialValue: detected ?? "")
        _form = State(initialValue: FormMapper.analyze(target.entry.config).model)
        let data = (try? target.entry.config.serialized()) ?? Data()
        _jsonText = State(initialValue: String(decoding: data, as: UTF8.self))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Picker("View", selection: viewBinding) {
                Text("Form").tag(EditView.form)
                Text("JSON").tag(EditView.json)
            }
            .pickerStyle(.segmented)
            .labelsHidden()

            field("Name") {
                TextField("my-mcp", text: $name).textFieldStyle(.roundedBorder)
            }

            if view == .form { formBody } else { jsonBody }

            if let error = validationError {
                Text(error).font(.callout).foregroundStyle(.red)
            }

            Divider()
            HStack {
                if !target.isNew {
                    Button("Remove…", role: .destructive) { confirmRemove = true }
                }
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Save") { save() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(view == .json && jsonError != nil)
            }
        }
        .padding(16)
        .frame(width: 460)
        .confirmationDialog(
            "Switching to Form view can’t fully represent this configuration. "
            + "These elements would be lost or altered:\n"
            + (lossWarning ?? []).joined(separator: "\n"),
            isPresented: Binding(get: { lossWarning != nil },
                                 set: { if !$0 { lossWarning = nil } }),
            titleVisibility: .visible
        ) {
            Button("Switch Anyway", role: .destructive) { forceSwitchToForm() }
            Button("Stay in JSON", role: .cancel) { lossWarning = nil }
        }
        .confirmationDialog(
            "Remove “\(target.name)”? A copy remains in Backups.",
            isPresented: $confirmRemove, titleVisibility: .visible
        ) {
            Button("Remove", role: .destructive) {
                state.remove(name: target.name)
                dismiss()
            }
        }
    }

    // MARK: view switching

    private var viewBinding: Binding<EditView> {
        Binding(get: { view }, set: { requested in
            guard requested != view else { return }
            if requested == .json {
                syncFormIntoJSON()
                view = .json
            } else {
                attemptSwitchToForm()
            }
        })
    }

    private func attemptSwitchToForm() {
        guard jsonError == nil, let config = parsedJSON() else { return }
        let analysis = FormMapper.analyze(config)
        if analysis.isLossless {
            adoptForm(analysis.model, config: config)
            view = .form
        } else {
            lossWarning = analysis.lost
        }
    }

    private func forceSwitchToForm() {
        guard let config = parsedJSON() else { lossWarning = nil; return }
        adoptForm(FormMapper.analyze(config).model, config: config)
        lossWarning = nil
        view = .form
    }

    private func adoptForm(_ model: FormModel, config: JSONValue) {
        form = model
        let detected = RemotePattern.detect(config)
        isRemote = detected != nil
        remoteURL = detected ?? ""
    }

    private func syncFormIntoJSON() {
        let data = (try? currentFormConfig().serialized()) ?? Data()
        jsonText = String(decoding: data, as: UTF8.self)
        jsonError = nil
    }

    private func parsedJSON() -> JSONValue? {
        do {
            let value = try JSONValue.parse(Data(jsonText.utf8))
            jsonError = nil
            return value
        } catch {
            jsonError = "Not valid JSON: \(error.localizedDescription)"
            return nil
        }
    }

    private func currentFormConfig() -> JSONValue {
        if isRemote {
            guard case .object(var object) = RemotePattern.make(url: remoteURL) else {
                return RemotePattern.make(url: remoteURL)
            }
            for (key, value) in form.additional { object[key] = value }
            return .object(object)
        }
        return FormMapper.serialize(form)
    }

    // MARK: form body

    @ViewBuilder private var formBody: some View {
        if isRemote {
            field("Server URL") {
                TextField("https://example.com/mcp", text: $remoteURL)
                    .textFieldStyle(.roundedBorder)
            }
            Text("Runs via npx mcp-remote — managed for you")
                .font(.caption).foregroundStyle(.secondary)
        } else {
            field("Command") {
                TextField("npx", text: $form.command).textFieldStyle(.roundedBorder)
            }
            field("Arguments") { argsEditor }
            field("Environment variables") { envEditor }
        }
        if !form.additional.isEmpty {
            DisclosureGroup(
                "\(form.additional.count) field(s) not editable here: "
                + form.additional.keys.sorted().joined(separator: ", ")
                + " — switch to JSON to edit"
            ) {
                Text(additionalPreview)
                    .font(.system(.caption, design: .monospaced))
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .font(.caption)
        }
    }

    private var additionalPreview: String {
        let data = (try? JSONValue.object(form.additional).serialized()) ?? Data()
        return String(decoding: data, as: UTF8.self)
    }

    private var argsEditor: some View {
        VStack(alignment: .leading, spacing: 4) {
            ForEach(form.args.indices, id: \.self) { index in
                HStack {
                    TextField("argument", text: $form.args[index])
                        .textFieldStyle(.roundedBorder)
                        .font(.system(.body, design: .monospaced))
                    Button { form.args.remove(at: index) } label: {
                        Image(systemName: "xmark.circle")
                    }.buttonStyle(.plain)
                }
            }
            Button("＋ Add argument") { form.args.append("") }
                .buttonStyle(.plain).font(.caption).foregroundStyle(.secondary)
        }
    }

    private var envEditor: some View {
        VStack(alignment: .leading, spacing: 4) {
            ForEach(form.env.keys.sorted(), id: \.self) { key in
                HStack {
                    Text(key).font(.system(.body, design: .monospaced))
                        .frame(width: 130, alignment: .leading)
                    if envRevealed.contains(key) {
                        TextField("value", text: envBinding(key))
                            .textFieldStyle(.roundedBorder)
                            .font(.system(.body, design: .monospaced))
                    } else {
                        SecureField("value", text: envBinding(key))
                            .textFieldStyle(.roundedBorder)
                    }
                    Button {
                        if envRevealed.contains(key) { envRevealed.remove(key) }
                        else { envRevealed.insert(key) }
                    } label: { Image(systemName: "eye") }.buttonStyle(.plain)
                    Button { form.env.removeValue(forKey: key) } label: {
                        Image(systemName: "xmark.circle")
                    }.buttonStyle(.plain)
                }
            }
            EnvAdder { key, value in form.env[key] = value }
        }
    }

    private func envBinding(_ key: String) -> Binding<String> {
        Binding(get: { form.env[key] ?? "" }, set: { form.env[key] = $0 })
    }

    // MARK: json body

    @ViewBuilder private var jsonBody: some View {
        TextEditor(text: $jsonText)
            .font(.system(.callout, design: .monospaced))
            .frame(height: 180)
            .overlay(RoundedRectangle(cornerRadius: 6)
                .stroke(jsonError == nil ? Color.secondary.opacity(0.3) : .red))
            .onChange(of: jsonText) { _ = parsedJSON() }
        if let error = jsonError {
            Text(error).font(.caption).foregroundStyle(.red)
        } else {
            Text("Tip: paste a README snippet — a {\"mcpServers\": {…}} wrapper is unwrapped automatically.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    // MARK: save

    private func save() {
        validationError = nil
        var config: JSONValue
        if view == .json {
            guard var parsed = parsedJSON() else { return }
            // Unwrap {"mcpServers": {"name": {...}}} paste convenience.
            if case .object(let outer) = parsed, outer.count == 1,
               case .object(let inner)? = outer["mcpServers"], inner.count == 1,
               let (pastedName, pastedConfig) = inner.first {
                if name.trimmingCharacters(in: .whitespaces).isEmpty { name = pastedName }
                parsed = pastedConfig
            }
            config = parsed
        } else {
            if isRemote {
                guard let url = URL(string: remoteURL),
                      let scheme = url.scheme?.lowercased(),
                      scheme == "http" || scheme == "https", url.host != nil else {
                    validationError = "Server URL must be a valid http(s) URL."
                    return
                }
            } else if form.command.trimmingCharacters(in: .whitespaces).isEmpty {
                validationError = "Command must not be empty."
                return
            }
            config = currentFormConfig()
        }
        let entry = MCPEntry(enabled: target.entry.enabled, config: config,
                             lastEditView: view)
        if let error = state.upsert(name: name, entry: entry,
                                    renamedFrom: target.isNew ? nil : target.name) {
            validationError = error
            return
        }
        dismiss()
    }

    private func field(_ label: String, @ViewBuilder content: () -> some View) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(label.uppercased()).font(.caption2).foregroundStyle(.secondary)
            content()
        }
    }
}

/// Two fields + button for adding an env var.
struct EnvAdder: View {
    var onAdd: (String, String) -> Void
    @State private var key = ""
    @State private var value = ""

    var body: some View {
        HStack {
            TextField("NAME", text: $key)
                .textFieldStyle(.roundedBorder).frame(width: 130)
                .font(.system(.body, design: .monospaced))
            TextField("value", text: $value).textFieldStyle(.roundedBorder)
            Button("＋") {
                let k = key.trimmingCharacters(in: .whitespaces)
                guard !k.isEmpty else { return }
                onAdd(k, value)
                key = ""; value = ""
            }.buttonStyle(.plain)
        }
    }
}
```

- [ ] **Step 2: Wire the sheet into the popover**

In `Sources/MCPEnabler/PopoverView.swift`:

Add to `PopoverView`:

```swift
    @State private var editTarget: EditTarget?
```

Change `mcpList`'s ForEach row to pass the callback and add the sheet modifier on the outer `VStack` (after `.frame(width: 380)`):

```swift
                ForEach(state.sortedNames, id: \.self) { name in
                    MCPRow(name: name) {
                        if let entry = state.store.mcps[name] {
                            editTarget = .existing(name: name, entry: entry)
                        }
                    }
                    Divider()
                }
```

```swift
        .sheet(item: $editTarget) { target in
            EditSheetView(target: target).environmentObject(state)
        }
```

In `MCPRow`, add the property and chevron button (replacing the `// Edit chevron arrives in Task 11.` comment):

```swift
    let onEdit: () -> Void
```

```swift
            Button(action: onEdit) {
                Image(systemName: "chevron.right").foregroundStyle(.secondary)
            }.buttonStyle(.plain)
```

- [ ] **Step 3: Build and manually verify**

Run: `swift build` — expected: no errors. Then run sandboxed as in Task 10 Step 4 and verify:

1. Chevron on `scoutbook` opens the sheet in **Form** view showing Name + Server URL (remote detected).
2. Segmented control switches to JSON showing the exact config; switch back — silent (lossless).
3. In JSON view, add `"headers": {"X": "y"}` → switch to Form → silent, and an "1 field(s) not editable here: headers" disclosure appears; save; reopen — still opens in **Form** (view memory), headers intact in JSON view.
4. In JSON view set `"args": ["ok", {"bad": 1}]` → switch to Form → the warning dialog lists `args[1] (object)` with **Stay in JSON** (cancel) and **Switch Anyway**; verify both branches.
5. Enter invalid JSON → red border + error, Save disabled, Form toggle does nothing.
6. Rename `scoutbook` to `scoutbook2`, save, verify rename in list; rename it back.
7. Remove… asks for confirmation; after Remove + Apply the entry leaves the sandbox config; a backup containing it exists in `.sandbox/store/backups/`.

- [ ] **Step 4: Run full test suite, then commit**

Run: `swift test`
Expected: all PASS.

```bash
git add Sources/MCPEnabler
git commit -m "feat: edit sheet with form/json views, view memory, and lossy-switch warning"
```

---

### Task 12: Add flows (Remote / Local) and Backups menu

**Files:**
- Modify: `Sources/MCPEnabler/PopoverView.swift`
- Create: `Sources/MCPEnabler/RestoreSheetView.swift`

**Interfaces:**
- Consumes: `EditTarget.new(template:)`, `RemotePattern.make`, `ConfigService.restoreClaudeConfig`, `BackupManager.backups(series:)`.
- Produces: footer **＋ Add** menu (Remote…/Local…), **Backups** menu (Reveal in Finder / Restore…), `RestoreSheetView`.

- [ ] **Step 1: Add the menus to the popover footer**

In `PopoverView`, add state:

```swift
    @State private var showRestore = false
```

Replace the `// Add / Backups menus arrive in Tasks 12–13.` comment in `footer` with:

```swift
            Menu("＋ Add") {
                Button("Remote Server…") {
                    editTarget = .new(template: RemotePattern.make(url: ""))
                }
                Button("Local Server…") {
                    editTarget = .new(template: .object([
                        "command": .string("npx"),
                        "args": .array([.string("-y"), .string("")]),
                    ]))
                }
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            Menu("Backups") {
                Button("Reveal in Finder") {
                    NSWorkspace.shared.activateFileViewerSelecting(
                        [state.service.backups.backupsDir])
                }
                Button("Restore…") { showRestore = true }
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
```

Add alongside the `.sheet(item:)` modifier:

```swift
        .sheet(isPresented: $showRestore) {
            RestoreSheetView().environmentObject(state)
        }
```

- [ ] **Step 2: Write the restore sheet**

`Sources/MCPEnabler/RestoreSheetView.swift`:

```swift
import SwiftUI
import MCPEnablerCore

struct RestoreSheetView: View {
    @EnvironmentObject var state: AppState
    @Environment(\.dismiss) private var dismiss
    @State private var backups: [URL] = []
    @State private var selection: URL?
    @State private var confirming = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Restore Claude config from a backup").font(.headline)
            Text("The current file is backed up first, then replaced by the "
                 + "selected backup.")
                .font(.caption).foregroundStyle(.secondary)
            List(backups, id: \.self, selection: $selection) { url in
                Text(url.lastPathComponent).font(.system(.callout, design: .monospaced))
            }
            .frame(height: 180)
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Restore…") { confirming = true }
                    .disabled(selection == nil)
            }
        }
        .padding(16)
        .frame(width: 460)
        .onAppear {
            var found = (try? state.service.backups.backups(
                series: "claude_desktop_config")) ?? []
            let original = state.service.backups.backupsDir
                .appendingPathComponent("claude_desktop_config.original.json")
            if FileManager.default.fileExists(atPath: original.path) {
                found.append(original)
            }
            backups = found
        }
        .confirmationDialog(
            "Replace Claude's config with \(selection?.lastPathComponent ?? "")?",
            isPresented: $confirming, titleVisibility: .visible
        ) {
            Button("Restore", role: .destructive) {
                guard let backup = selection else { return }
                do {
                    try state.service.restoreClaudeConfig(
                        from: backup, mergedWith: state.store)
                    state.reload()
                    state.showRestartPrompt = true
                    dismiss()
                } catch {
                    state.lastError = error.localizedDescription
                }
            }
        }
    }
}
```

- [ ] **Step 3: Build and manually verify**

Run: `swift build`, then run sandboxed (Task 10 Step 4 command) and verify:

1. **＋ Add ▸ Remote Server…** opens the sheet with empty Name + Server URL; saving with URL `https://example.com/mcp` adds an enabled entry whose JSON is the canonical mcp-remote shape; Apply writes it to the sandbox config.
2. **＋ Add ▸ Local Server…** opens the generic form pre-filled `command: npx`, args `-y` + empty row.
3. In a new Add sheet's JSON view, paste `{"mcpServers": {"pasted": {"command": "echo"}}}` → Save → name auto-fills to `pasted`, config unwrapped.
4. Save with a duplicate name → inline validation error, sheet stays open.
5. **Backups ▸ Reveal in Finder** opens the backups folder. **Backups ▸ Restore…** lists timestamped backups plus the original; restoring one replaces the sandbox config and reconciles (list updates), and offers restart.

- [ ] **Step 4: Run full test suite, then commit**

Run: `swift test`
Expected: all PASS.

```bash
git add Sources/MCPEnabler
git commit -m "feat: add-remote/add-local flows and backup restore UI"
```

---

### Task 13: Restart Claude, config file watcher, launch at login

**Files:**
- Create: `Sources/MCPEnabler/ClaudeRestarter.swift`
- Create: `Sources/MCPEnabler/FileWatcher.swift`
- Modify: `Sources/MCPEnabler/AppState.swift`
- Modify: `Sources/MCPEnabler/PopoverView.swift`

**Interfaces:**
- Consumes: `AppState.reload()`, `AppPaths.claudeConfigURL`.
- Produces: `enum ClaudeRestarter { static func restart(completion: @escaping (String?) -> Void) }` (nil = success, else error message); `final class FileWatcher { init(url: URL, onChange: @escaping () -> Void); func start(); func stop() }`; `AppState.restartClaude()`; launch-at-login toggle.

- [ ] **Step 1: Write ClaudeRestarter**

`Sources/MCPEnabler/ClaudeRestarter.swift`:

```swift
import AppKit

enum ClaudeRestarter {
    static let bundleID = "com.anthropic.claudefordesktop"
    static let appURL = URL(fileURLWithPath: "/Applications/Claude.app")

    /// Gracefully terminate Claude (never force-kill), wait up to 15 s, relaunch.
    /// Calls completion on the main queue with nil on success or an error message.
    static func restart(completion: @escaping (String?) -> Void) {
        guard FileManager.default.fileExists(atPath: appURL.path) else {
            completion("Claude.app was not found at \(appURL.path).")
            return
        }
        let running = NSRunningApplication.runningApplications(
            withBundleIdentifier: bundleID)
        running.forEach { $0.terminate() }

        DispatchQueue.global().async {
            let deadline = Date().addingTimeInterval(15)
            while Date() < deadline {
                let still = NSRunningApplication.runningApplications(
                    withBundleIdentifier: bundleID)
                if still.allSatisfy(\.isTerminated) || still.isEmpty { break }
                Thread.sleep(forTimeInterval: 0.25)
            }
            let stillRunning = !NSRunningApplication.runningApplications(
                withBundleIdentifier: bundleID).isEmpty
            DispatchQueue.main.async {
                if stillRunning {
                    completion("Claude didn’t quit (it may be showing a dialog). "
                               + "Quit it manually, then it will relaunch.")
                }
                NSWorkspace.shared.openApplication(
                    at: appURL,
                    configuration: NSWorkspace.OpenConfiguration()
                ) { _, error in
                    DispatchQueue.main.async {
                        if !stillRunning { completion(error?.localizedDescription) }
                    }
                }
            }
        }
    }
}
```

- [ ] **Step 2: Write FileWatcher**

`Sources/MCPEnabler/FileWatcher.swift`:

```swift
import Foundation

/// Watches the parent directory of `url` (atomic writes replace the inode, so
/// watching the file itself misses them) and fires when the file's modification
/// date changes.
final class FileWatcher {
    private let url: URL
    private let onChange: () -> Void
    private var source: DispatchSourceFileSystemObject?
    private var lastModified: Date?

    init(url: URL, onChange: @escaping () -> Void) {
        self.url = url
        self.onChange = onChange
    }

    func start() {
        stop()
        lastModified = modificationDate()
        let dirFD = open(url.deletingLastPathComponent().path, O_EVTONLY)
        guard dirFD >= 0 else { return }
        let source = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: dirFD, eventMask: [.write, .rename, .delete],
            queue: .main)
        source.setEventHandler { [weak self] in self?.checkForChange() }
        source.setCancelHandler { close(dirFD) }
        source.resume()
        self.source = source
    }

    func stop() {
        source?.cancel()
        source = nil
    }

    private func checkForChange() {
        let current = modificationDate()
        guard current != lastModified else { return }
        lastModified = current
        onChange()
    }

    private func modificationDate() -> Date? {
        (try? FileManager.default.attributesOfItem(atPath: url.path))?[.modificationDate]
            as? Date
    }
}
```

- [ ] **Step 3: Wire into AppState and the popover**

In `AppState.swift` add a property, start the watcher in `init`, and add the restart method:

```swift
    private var watcher: FileWatcher?
```

At the end of `init`:

```swift
        watcher = FileWatcher(url: service.paths.claudeConfigURL) { [weak self] in
            self?.reload()
        }
        watcher?.start()
```

New method on `AppState`:

```swift
    func restartClaude() {
        showRestartPrompt = false
        ClaudeRestarter.restart { [weak self] errorMessage in
            self?.lastError = errorMessage
        }
    }
```

In `PopoverView.footer`, replace the placeholder restart button:

```swift
                Button("Restart Claude") { state.restartClaude() }
```

Add launch-at-login. In `PopoverView.swift` add `import ServiceManagement` and this control in `footer`, before `Spacer()`:

```swift
            Toggle("Launch at login", isOn: Binding(
                get: { SMAppService.mainApp.status == .enabled },
                set: { on in
                    do {
                        if on { try SMAppService.mainApp.register() }
                        else { try SMAppService.mainApp.unregister() }
                    } catch {
                        state.lastError = "Launch at login needs the built app "
                            + "bundle (run scripts/build-app.sh): \(error.localizedDescription)"
                    }
                }))
                .font(.caption)
                .toggleStyle(.checkbox)
```

- [ ] **Step 4: Build and manually verify**

Run: `swift build`, then run sandboxed (Task 10 Step 4 command) and verify:

1. Edit `.sandbox/claude_desktop_config.json` externally (add a server `"handmade": {"command": "echo"}`) while the app runs → within a second the popover shows `handmade` imported and enabled (watcher + reconcile).
2. Delete a server externally → missing banner appears without reopening the popover.
3. Toggle an MCP, Apply → restart prompt appears; **Restart Claude** quits and relaunches the real Claude Desktop (safe: only the sandbox config was modified — the real config is untouched; expect Claude to relaunch with unchanged MCPs). If Claude isn't installed/running, the error path shows a message instead.
4. Launch-at-login toggle under `swift run` shows the friendly bundle error when toggled — expected until Task 14's .app build.

- [ ] **Step 5: Run full test suite, then commit**

Run: `swift test`
Expected: all PASS.

```bash
git add Sources/MCPEnabler
git commit -m "feat: restart Claude, live config watching, launch-at-login toggle"
```

---

### Task 14: App bundle build script + final verification

**Files:**
- Create: `scripts/build-app.sh`
- Create: `README.md`

**Interfaces:**
- Consumes: the built `MCPEnabler` executable.
- Produces: `build/MCP Enabler.app` (LSUIElement, ad-hoc signed).

- [ ] **Step 1: Write the build script**

`scripts/build-app.sh`:

```bash
#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."

APP="build/MCP Enabler.app"
swift build -c release

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp .build/release/MCPEnabler "$APP/Contents/MacOS/MCP Enabler"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key><string>MCP Enabler</string>
    <key>CFBundleIdentifier</key><string>com.dlaporte.mcp-enabler</string>
    <key>CFBundleName</key><string>MCP Enabler</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>CFBundleVersion</key><string>1</string>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHumanReadableCopyright</key><string></string>
</dict>
</plist>
PLIST

codesign --force --sign - "$APP"
echo "Built: $APP"
echo "Install: cp -R \"$APP\" /Applications/"
```

Run: `chmod +x scripts/build-app.sh && ./scripts/build-app.sh`
Expected: `Built: build/MCP Enabler.app`.

- [ ] **Step 2: Write the README**

`README.md`:

```markdown
# MCP Enabler

A lightweight macOS menu bar app to enable/disable/edit the MCP servers in
Claude Desktop's `claude_desktop_config.json` — with automatic backups of
everything it touches. See `docs/superpowers/specs/` for the full design.

## Build & install

    ./scripts/build-app.sh
    cp -R "build/MCP Enabler.app" /Applications/
    open "/Applications/MCP Enabler.app"

## Development

    swift test                      # unit tests (never touch real config)
    MCP_ENABLER_CLAUDE_CONFIG="$PWD/.sandbox/claude_desktop_config.json" \
    MCP_ENABLER_STORE_DIR="$PWD/.sandbox/store" \
    swift run MCPEnabler            # sandboxed dev run

## Data & backups

- Master MCP list: `~/Library/Application Support/MCP Enabler/mcps.json`
- Backups (last 20 per file + permanent first-run original):
  `~/Library/Application Support/MCP Enabler/backups/`
- Claude's config is rewritten only on Apply; every other key in it is preserved.
```

- [ ] **Step 3: End-to-end verification against the REAL config (first live use)**

With the user's awareness (this is the tool's intended purpose):

```bash
open "build/MCP Enabler.app"
```

1. Popover lists the real MCPs (scoutbook, aws-mcp, service-now) enabled.
2. `~/Library/Application Support/MCP Enabler/backups/claude_desktop_config.original.json` exists and matches the pre-run config.
3. Toggle `aws-mcp` off → Apply → `claude_desktop_config.json` has 2 servers, `preferences` block byte-identical in content; a timestamped backup holds the 3-server version.
4. Toggle it back on → Apply → 3 servers again. Decline restart both times, then Restart Claude once and confirm Claude relaunches.
5. Launch-at-login toggle now works from the installed bundle.

- [ ] **Step 4: Run full suite one last time and commit**

Run: `swift test`
Expected: all PASS.

```bash
git add scripts/build-app.sh README.md
git commit -m "feat: app bundle build script and README"
```

---

## Spec coverage checklist (self-review)

- Toggle per MCP, menu bar app → Tasks 10, 14
- Master store source of truth, schema with `lastEditView` → Task 3
- Claude config read/write preserving non-mcpServers keys, atomic, missing/malformed handling → Tasks 2, 5
- Reconciliation (4 rules incl. wipe flag, never silent delete) → Tasks 8, 9, 10
- Backups: pre-write both files, original forever, keep 20, reveal/restore UI → Tasks 4, 9, 12
- Edit sheet: Form/JSON toggle, per-MCP view memory, remote detection (Name+URL), args/env editors with masking, Additional fields, lossy warning with Stay in JSON default / Switch Anyway, invalid-JSON gating → Tasks 6, 7, 11
- Add Remote / Add Local templates, README-paste unwrap, duplicate-name validation → Task 12
- Apply → offer restart; graceful terminate + relaunch; file watcher; missing banner Restore/Mark disabled → Tasks 10, 13
- Launch at login, .app bundle, LSUIElement → Tasks 13, 14
- Corrupt master store rebuilt with preserved corrupt file → Tasks 3, 9

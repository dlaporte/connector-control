import XCTest
import ConnectorControlTestSupport
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/CollectionsFileTests.cs
final class CollectionsFileTests: XCTestCase {
    var tempDir: TempDir!
    override func setUpWithError() throws { tempDir = TempDir(prefix: "collections") }
    override func tearDownWithError() throws { tempDir.dispose() }

    static let sample = CollectionsFile(collections: [
        "Personal": .init(kind: .local, fileName: nil, relativeToStore: nil, origin: nil, needs: [:], publish: nil,
                          provenance: ["dbt": .init(from: "Data team", author: "Acme Data Platform", date: "2026-09-21")]),
        "Data team": .init(kind: .synced, fileName: "data-team.json", relativeToStore: "../mcp/data-team.json", origin: "6f1c4a2e-1b8d-4b0e-9f0a-3c2d7e8a91e2",
                           needs: ["dbt": ["DBT_TOKEN": .init(hint: "cloud.getdbt.com ▸ Account ▸ API tokens", pointer: JSONPointer(["env", "DBT_TOKEN"])),
                                           "server_path": .init(hint: "your ledger clone, then dist/index.js", pointer: JSONPointer(["args", "1"]))]],
                           publish: nil, provenance: [:]),
        "Consulting": .init(kind: .local, fileName: nil, relativeToStore: nil, origin: nil, needs: [:],
                            publish: .init(slug: "consulting", origin: "0c9b7d1e-5a3f-4f2c-8e6d-2b1a9c8d7e6f",
                                           intent: PublishIntent(shareValues: ["tax": ["LOG_LEVEL"]],
                                                                 pathMarks: ["ledger": [JSONPointer(["args", "1"]): .init(name: "server_path", hint: "your ledger clone, then dist/index.js",
                                                                                                                          value: "/Users/you/ledger/dist/index.js")]],
                                                                 hints: ["tax": ["ANTHROPIC_API_KEY": "console.anthropic.com ▸ API keys"]])),
                            provenance: [:]),
    ])

    func testEncodeDecodeRoundTripAndGolden() throws {
        XCTAssertEqual(try CollectionsFile.decode(Self.sample.encode()), Self.sample)
        // Written once from this Mac's Foundation (CONNECTOR_CONTROL_UPDATE_GOLDENS=1), then compared
        // byte for byte here and by the C# mirror, so both platforms write the same sidecar.
        let url = Fixtures.url("collections.json")
        let data = try Self.sample.encode().serialized()
        if ProcessInfo.processInfo.environment["CONNECTOR_CONTROL_UPDATE_GOLDENS"] == "1" { try data.write(to: url) }
        XCTAssertEqual(data, try Data(contentsOf: url))
        XCTAssertEqual(try CollectionsFile.decode(JSONValue.parse(Data(contentsOf: url))), Self.sample)
    }

    func testKindDefaultsToLocal() {
        XCTAssertEqual(Self.sample.kind(of: "Data team"), .synced)
        XCTAssertEqual(Self.sample.kind(of: "Personal"), .local)
        XCTAssertEqual(Self.sample.kind(of: "Never heard of it"), .local)
    }

    func testLoadTreatsMissingAndCorruptAsEmpty() throws {
        let url = tempDir.file("collections.json")
        XCTAssertEqual(CollectionsFile.load(from: url), CollectionsFile(collections: [:]))
        try Data("{not json".utf8).write(to: url)
        XCTAssertEqual(CollectionsFile.load(from: url), CollectionsFile(collections: [:]))
        XCTAssertTrue(FileManager.default.fileExists(atPath: url.path), "load never moves a file aside")
    }

    func testLoadIfReadableTellsAnUnreadableFileFromAnEmptyOne() throws {
        let url = tempDir.file("collections.json")
        XCTAssertEqual(CollectionsFile.loadIfReadable(from: url), CollectionsFile(collections: [:]), "missing is empty")
        try CollectionsFile(collections: [:]).save(to: url, staging: nil)
        XCTAssertEqual(CollectionsFile.loadIfReadable(from: url), CollectionsFile(collections: [:]), "no entries is empty")
        try Data("{not json".utf8).write(to: url)
        XCTAssertNil(CollectionsFile.loadIfReadable(from: url), "a file that cannot be parsed is unreadable")
        try Data("{\"version\":2,\"collections\":{}}".utf8).write(to: url)
        XCTAssertNil(CollectionsFile.loadIfReadable(from: url), "a file that cannot be decoded is unreadable")
        try Data().write(to: url)
        XCTAssertNil(CollectionsFile.loadIfReadable(from: url), "a file truncated to nothing is unreadable")
        XCTAssertTrue(FileManager.default.fileExists(atPath: url.path), "load never moves a file aside")
    }

    func testSaveIsOwnerOnlyAndReloads() throws {
        let url = tempDir.file("collections.json")
        try Self.sample.save(to: url, staging: nil)
        let mode = try XCTUnwrap(FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? Int)
        XCTAssertEqual(mode, 0o600)
        XCTAssertEqual(CollectionsFile.load(from: url), Self.sample)
    }

    func testReconcileDropsNamesTheStoreNoLongerHas() {
        let store = MasterStore(activeCollection: "Personal", collections: ["Personal": Collection(), "Consulting": Collection()])
        let reconciled = Self.sample.reconciled(with: store)
        XCTAssertEqual(Set(reconciled.collections.keys), ["Personal", "Consulting"])
    }

    func testAnUnknownVersionDecodesAsMalformed() {
        XCTAssertThrowsError(try CollectionsFile.decode(.object(["version": .int(9), "collections": .object([:])])))
    }

    /// No release wrote a path mark without the value it was made on, but a build from before
    /// values were kept may have: such a mark loads as the pointer-only mark it was.
    func testAPathMarkWithNoValueStillLoads() throws {
        let json = try JSONValue.parse(Data("""
            {"version": 1, "collections": {"Consulting": {"kind": "local", "publish": {
              "slug": "consulting", "origin": "o", "shareValues": {}, "hints": {},
              "paths": {"ledger": {"/args/1": {"name": "server_path", "hint": null}}}}}}}
            """.utf8))
        let marks = try CollectionsFile.decode(json).collections["Consulting"]?.publish?.intent.pathMarks["ledger"]
        XCTAssertEqual(marks, [JSONPointer(["args", "1"]): .init(name: "server_path", hint: nil, value: nil)])
    }

    /// A local collection with nothing to say is absent from the file, not written as an empty
    /// entry: the sidecar only ever records what the master list cannot.
    func testALocalCollectionWithNothingToSayIsNotWritten() throws {
        let file = CollectionsFile(collections: ["Personal": .local, "Consulting": Self.sample.collections["Consulting"]!])
        XCTAssertEqual(file.encode().value(at: JSONPointer(["collections", "Personal"])), nil)
        XCTAssertEqual(try CollectionsFile.decode(file.encode()).collections.keys.sorted(), ["Consulting"])
        XCTAssertEqual(file.kind(of: "Personal"), .local)
    }
}

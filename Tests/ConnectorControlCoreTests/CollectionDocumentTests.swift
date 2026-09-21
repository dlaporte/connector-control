import XCTest
import ConnectorControlTestSupport
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/CollectionDocumentTests.cs
final class CollectionDocumentTests: XCTestCase {
    static let sample = CollectionDocument(
        name: "Data team", author: "Acme Data Platform", origin: "6f1c4a2e-1b8d-4b0e-9f0a-3c2d7e8a91e2",
        exported: "2026-09-21T14:02:11Z",
        connectors: [
            "github": .init(launcher: .remote(.init(url: "https://mcp.github.com/", auth: .automatic, package: "mcp-remote", extraArgs: [])), env: [:], needs: [:], additional: [:]),
            "notion": .init(launcher: .remote(.init(url: "https://mcp.notion.com/", auth: .bearer, package: "mcp-remote", extraArgs: [])), env: [:], needs: ["token": "notion.so ▸ integrations"], additional: [:]),
            "dbt": .init(launcher: .local(.init(command: "npx", args: ["-y", "@dbt/mcp"], platform: .mac)),
                         env: ["DBT_TOKEN": .hint("cloud.getdbt.com ▸ API tokens"), "DBT_REGION": .value("us")], needs: [:], additional: [:]),
            "ledger": .init(launcher: .local(.init(command: "node", args: ["${CC_NEEDS:server_path}"], platform: .mac)),
                            env: [:], needs: ["server_path": "your ledger clone, then dist/index.js"], additional: ["type": .string("stdio")]),
        ])

    func testEncodeDecodeRoundTrip() throws {
        let json = Self.sample.encode()
        XCTAssertEqual(json.value(at: JSONPointer(["connectorControlCollection"])), .int(1))
        XCTAssertEqual(try CollectionDocument.decode(json), Self.sample)
    }

    func testGoldenBytesMatchTheFixture() throws {
        // Written once from this Mac's Foundation (CONNECTOR_CONTROL_UPDATE_GOLDENS=1), then compared
        // byte for byte here and by the C# mirror, so both platforms write the same document.
        let url = Fixtures.url("collection.json")
        let data = try Self.sample.serialized()
        if ProcessInfo.processInfo.environment["CONNECTOR_CONTROL_UPDATE_GOLDENS"] == "1" { try data.write(to: url) }
        XCTAssertEqual(data, try Data(contentsOf: url))
        XCTAssertEqual(try CollectionDocument.decode(try Data(contentsOf: url)), Self.sample)
    }

    func testANewerFormatIsRefused() {
        var json = Self.sample.encode()
        json = json.replacing(at: JSONPointer(["connectorControlCollection"]), with: .int(2))!
        XCTAssertThrowsError(try CollectionDocument.decode(json)) { XCTAssertEqual($0 as? CollectionDocumentError, .newerFormat(2)) }
        XCTAssertThrowsError(try CollectionDocument.decode(.object(["name": .string("x")]))) {
            guard case .malformed? = $0 as? CollectionDocumentError else { return XCTFail("\($0)") }
        }
    }

    func testFileNameIsTheSlug() {
        XCTAssertEqual(Self.sample.fileName, "data-team.json")
    }

    func testRenderProducesThisPlatformsLaunchersWithMarkers() throws {
        let rendered = Self.sample.render()
        XCTAssertTrue(rendered.excluded.isEmpty)
        let notion = try XCTUnwrap(rendered.connectors["notion"])
        XCTAssertEqual(notion.config.value(at: JSONPointer(["command"])), .string("npx"))
        XCTAssertEqual(notion.config.value(at: JSONPointer(["env", "AUTH_HEADER"])), .string("Bearer ${CC_NEEDS:token}"))
        XCTAssertEqual(notion.needs["token"], RenderedNeed(hint: "notion.so ▸ integrations", pointer: JSONPointer(["env", "AUTH_HEADER"])))
        XCTAssertNil(notion.authoredOn)
        let dbt = try XCTUnwrap(rendered.connectors["dbt"])
        XCTAssertEqual(dbt.config.value(at: JSONPointer(["env", "DBT_TOKEN"])), .string("${CC_NEEDS:DBT_TOKEN}"))
        XCTAssertEqual(dbt.config.value(at: JSONPointer(["env", "DBT_REGION"])), .string("us"))
        XCTAssertEqual(dbt.needs["DBT_TOKEN"]?.pointer, JSONPointer(["env", "DBT_TOKEN"]))
        XCTAssertEqual(dbt.needs["DBT_TOKEN"]?.hint, "cloud.getdbt.com ▸ API tokens")
        XCTAssertEqual(dbt.authoredOn, .mac)
        let ledger = try XCTUnwrap(rendered.connectors["ledger"])
        XCTAssertEqual(ledger.needs["server_path"], RenderedNeed(hint: "your ledger clone, then dist/index.js", pointer: JSONPointer(["args", "0"])))
        XCTAssertEqual(ledger.config.value(at: JSONPointer(["type"])), .string("stdio"))
    }

    func testExportStripsSecretsSharesTickedValuesAndMarksPaths() throws {
        let notion = RemotePattern.encode(RemoteConfig(url: "https://mcp.notion.com/", auth: .bearer(token: "secret-1"), extraArgs: [], passthroughEnv: [:], package: "mcp-remote"))
        let dbt: JSONValue = .object(["command": .string("npx"), "args": .array([.string("-y"), .string("@dbt/mcp")]),
                                      "env": .object(["DBT_TOKEN": .string("tok"), "DBT_REGION": .string("us")])])
        let ledger: JSONValue = .object(["command": .string("node"), "args": .array([.string("/Users/d/ledger/dist/index.js")]), "type": .string("stdio")])
        let intent = PublishIntent(
            shareValues: ["dbt": ["DBT_REGION"]],
            pathMarks: ["ledger": [JSONPointer(["args", "0"]): .init(name: "server_path", hint: "your ledger clone")]],
            hints: ["dbt": ["DBT_TOKEN": "cloud.getdbt.com"], "notion": ["token": "notion.so"]])
        let doc = CollectionDocument.export(name: "Consulting", author: nil, origin: "o-1", exported: "2026-09-21T15:00:00Z",
                                            connectors: ["notion": notion, "dbt": dbt, "ledger": ledger], intent: intent)
        guard case .remote(let r) = try XCTUnwrap(doc.connectors["notion"]).launcher else { return XCTFail("notion is not remote") }
        XCTAssertEqual(r.auth, .bearer)
        XCTAssertEqual(doc.connectors["notion"]?.needs, ["token": "notion.so"])
        XCTAssertFalse(doc.encode().serializedString.contains("secret-1"))
        XCTAssertEqual(doc.connectors["dbt"]?.env, ["DBT_TOKEN": .hint("cloud.getdbt.com"), "DBT_REGION": .value("us")])
        guard case .local(let l) = try XCTUnwrap(doc.connectors["ledger"]).launcher else { return XCTFail("ledger is not local") }
        XCTAssertEqual(l.args, ["${CC_NEEDS:server_path}"])
        XCTAssertEqual(l.platform, .mac)
        XCTAssertEqual(doc.connectors["ledger"]?.needs, ["server_path": "your ledger clone"])
        XCTAssertEqual(doc.connectors["ledger"]?.additional, ["type": .string("stdio")])
    }

    func testExportKeepsAnUnfilledMarkerAsANeed() throws {
        let copy: JSONValue = .object(["command": .string("node"), "args": .array([.string("${CC_NEEDS:server_path}")])])
        let doc = CollectionDocument.export(name: "x", author: nil, origin: nil, exported: "2026-09-21T15:00:00Z", connectors: ["ledger": copy], intent: .none)
        XCTAssertEqual(doc.connectors["ledger"]?.needs, ["server_path": nil])
    }

    func testARemoteConnectorKeepsItsAdditionalFieldsThroughExportAndRender() throws {
        let encoded = RemotePattern.encode(RemoteConfig(url: "https://mcp.example.com/", auth: .automatic, package: "mcp-remote"))
        guard case .object(var object) = encoded else { return XCTFail("encode did not produce an object") }
        object["type"] = .string("stdio")
        let config: JSONValue = .object(object)
        let doc = CollectionDocument.export(name: "x", author: nil, origin: nil, exported: "2026-09-21T15:00:00Z",
                                            connectors: ["svc": config], intent: .none)
        XCTAssertEqual(doc.connectors["svc"]?.additional, ["type": .string("stdio")])
        let rendered = try XCTUnwrap(doc.render().connectors["svc"])
        XCTAssertEqual(rendered.config.value(at: JSONPointer(["type"])), .string("stdio"))
        XCTAssertEqual(rendered.config.value(at: JSONPointer(["command"])), .string("npx"))
    }

    func testRenderMarksTheHeaderValueAndClientSecret() throws {
        let doc = CollectionDocument(
            name: "x", author: nil, origin: nil, exported: "2026-09-21T15:00:00Z",
            connectors: [
                "svc-header": .init(launcher: .remote(.init(url: "https://mcp.example.com/", auth: .header(name: "X-Api-Key"),
                                                            package: "mcp-remote", extraArgs: [])),
                                    env: [:], needs: ["header_value": "vendor dashboard \u{25b8} API keys"], additional: [:]),
                "svc-oauth": .init(launcher: .remote(.init(url: "https://mcp.example.com/", auth: .oauthClient(clientId: "id-1", scopes: "read write"),
                                                           package: "mcp-remote", extraArgs: [])),
                                   env: [:], needs: ["client_secret": "vendor dashboard \u{25b8} OAuth apps"], additional: [:]),
            ])
        let rendered = doc.render()
        let header = try XCTUnwrap(rendered.connectors["svc-header"])
        XCTAssertEqual(header.config.value(at: JSONPointer(["env", "AUTH_HEADER"])), .string("${CC_NEEDS:header_value}"))
        XCTAssertEqual(header.config.value(at: JSONPointer(["args", "4"])), .string("X-Api-Key:${AUTH_HEADER}"))
        XCTAssertEqual(header.needs["header_value"],
                       RenderedNeed(hint: "vendor dashboard \u{25b8} API keys", pointer: JSONPointer(["env", "AUTH_HEADER"])))

        let oauth = try XCTUnwrap(rendered.connectors["svc-oauth"])
        let blob = try XCTUnwrap(oauth.config.value(at: JSONPointer(["args", "4"])))
        guard case .string(let blobText) = blob else { return XCTFail("blob is not a string") }
        XCTAssertTrue(blobText.contains("${CC_NEEDS:client_secret}"))
        XCTAssertTrue(blobText.contains("\"id-1\""))
        XCTAssertEqual(oauth.needs["client_secret"],
                       RenderedNeed(hint: "vendor dashboard \u{25b8} OAuth apps", pointer: JSONPointer(["args", "4"])))
    }

    func testExportKeepsTheAuthKindAndNonSecretFieldsForEveryKind() throws {
        let automatic = RemotePattern.encode(RemoteConfig(url: "https://mcp.example.com/", auth: .automatic, package: "mcp-remote"))
        let bearer = RemotePattern.encode(RemoteConfig(url: "https://mcp.example.com/", auth: .bearer(token: "secret-bearer"), package: "mcp-remote"))
        let header = RemotePattern.encode(RemoteConfig(url: "https://mcp.example.com/", auth: .header(name: "X-Api-Key", value: "secret-header"), package: "mcp-remote"))
        let oauth = RemotePattern.encode(RemoteConfig(url: "https://mcp.example.com/",
                                                       auth: .oauthClient(clientID: "id-1", clientSecret: "secret-oauth", scopes: "read write"),
                                                       package: "mcp-remote"))
        let doc = CollectionDocument.export(name: "x", author: nil, origin: nil, exported: "2026-09-21T15:00:00Z",
                                            connectors: ["automatic": automatic, "bearer": bearer, "header": header, "oauth": oauth], intent: .none)
        guard case .remote(let a) = try XCTUnwrap(doc.connectors["automatic"]).launcher else { return XCTFail("automatic is not remote") }
        XCTAssertEqual(a.auth, .automatic)
        guard case .remote(let b) = try XCTUnwrap(doc.connectors["bearer"]).launcher else { return XCTFail("bearer is not remote") }
        XCTAssertEqual(b.auth, .bearer)
        guard case .remote(let h) = try XCTUnwrap(doc.connectors["header"]).launcher else { return XCTFail("header is not remote") }
        XCTAssertEqual(h.auth, .header(name: "X-Api-Key"))
        guard case .remote(let o) = try XCTUnwrap(doc.connectors["oauth"]).launcher else { return XCTFail("oauth is not remote") }
        XCTAssertEqual(o.auth, .oauthClient(clientId: "id-1", scopes: "read write"))
        let serialized = doc.encode().serializedString
        for secret in ["secret-bearer", "secret-header", "secret-oauth"] {
            XCTAssertFalse(serialized.contains(secret))
        }
    }

    func testCredentialWarningsNameThePosition() {
        let config: JSONValue = .object(["command": .string("npx"), "args": .array([.string("-y"), .string("tax-mcp"), .string("--key"), .string("sk-live-9f3a")])])
        XCTAssertEqual(CollectionDocument.credentialWarnings(config, sharedEnv: []), ["args[3] looks like a credential"])
    }

    func testCredentialWarningsCoverHeaderPairsAndSharedValues() {
        let config: JSONValue = .object([
            "command": .string("npx"),
            "args": .array([.string("--header"), .string("X-Key: sk-live-1")]),
            "env": .object(["API_TOKEN": .string("ghp_shared123"), "OTHER": .string("sk-live-should-be-ignored")]),
        ])
        // "X-Key: sk-live-1" has a space, so the whole string is never flagged; the part after
        // ": " is. OTHER isn't in sharedEnv, so its credential-shaped value is never scanned.
        XCTAssertEqual(CollectionDocument.credentialWarnings(config, sharedEnv: ["API_TOKEN"]),
                       ["args[1] looks like a credential", "env.API_TOKEN looks like a credential"])
    }
}

private extension JSONValue {
    var serializedString: String { String(decoding: (try? serialized()) ?? Data(), as: UTF8.self) }
}

import XCTest
import ConnectorControlTestSupport
@testable import ConnectorControlCore

/// Mirror: windows/tests/ConnectorControl.Core.Tests/CollectionDocumentTests.cs
final class CollectionDocumentTests: XCTestCase {
    /// Shared with the State suite, which subscribes to this document on disk.
    static let sample = CollectionDocumentSamples.dataTeam

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
            pathMarks: ["ledger": [JSONPointer(["args", "0"]): .init(name: "server_path", hint: "your ledger clone",
                                                                     value: "/Users/d/ledger/dist/index.js")]],
            hints: ["dbt": ["DBT_TOKEN": "cloud.getdbt.com"], "notion": ["token": "notion.so"]])
        let doc = try CollectionDocument.export(name: "Consulting", author: nil, origin: "o-1", exported: "2026-09-21T15:00:00Z",
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
        let doc = try CollectionDocument.export(name: "x", author: nil, origin: nil, exported: "2026-09-21T15:00:00Z", connectors: ["ledger": copy], intent: .none)
        XCTAssertEqual(doc.connectors["ledger"]?.needs, ["server_path": nil])
    }

    // MARK: - Path marks follow their argument

    private func mark(_ value: String?, name: String = "path") -> PublishIntent.PathMark {
        PublishIntent.PathMark(name: name, hint: nil, value: value)
    }

    private func arg(_ index: Int) -> JSONPointer { JSONPointer(["args", String(index)]) }

    func testAMarkStaysOnItsArgumentOrFollowsItsValue() {
        let marks = [arg(1): mark("/Users/d/srv.js")]
        XCTAssertEqual(PublishIntent.placePathMarks(marks, in: ["-y", "/Users/d/srv.js"]).placed,
                       [1: mark("/Users/d/srv.js")], "where it was marked")
        XCTAssertEqual(PublishIntent.placePathMarks(marks, in: ["--quiet", "-y", "/Users/d/srv.js"]).placed,
                       [2: mark("/Users/d/srv.js")], "an argument inserted above: the mark follows the path")
        XCTAssertEqual(PublishIntent.placePathMarks(marks, in: ["/Users/d/srv.js"]).placed,
                       [0: mark("/Users/d/srv.js")], "one removed above")
        let reordered = PublishIntent.placePathMarks(marks, in: ["/Users/d/srv.js", "-y"])
        XCTAssertEqual(reordered.placed, [0: mark("/Users/d/srv.js")], "reordered")
        XCTAssertEqual(reordered.unresolved, [:])
        // Still where it was marked, so a second copy elsewhere does not make it ambiguous.
        XCTAssertEqual(PublishIntent.placePathMarks(marks, in: ["/Users/d/srv.js", "/Users/d/srv.js"]).placed,
                       [1: mark("/Users/d/srv.js")])
    }

    func testAMarkThatFindsNoArgumentIsUnresolved() {
        let marks = [arg(1): mark("/Users/d/srv.js")]
        let edited = PublishIntent.placePathMarks(marks, in: ["-y", "/Users/d/other.js"])
        XCTAssertEqual(edited.placed, [:])
        XCTAssertEqual(edited.unresolved, marks, "its value edited away")
        let twice = PublishIntent.placePathMarks(marks, in: ["/Users/d/srv.js", "-y", "/Users/d/srv.js"])
        XCTAssertEqual(twice.placed, [:])
        XCTAssertEqual(twice.unresolved, marks, "moved, and held by two arguments: which one it was is anybody's guess")
    }

    func testTwoMarksCannotShareOneArgument() {
        // Both were made on the same path; with one copy edited away, the one left can carry only one.
        let marks = [arg(0): mark("/p", name: "a"), arg(1): mark("/p", name: "b")]
        let placement = PublishIntent.placePathMarks(marks, in: ["/p", "/q"])
        XCTAssertEqual(placement.placed, [0: mark("/p", name: "a")])
        XCTAssertEqual(placement.unresolved, [arg(1): mark("/p", name: "b")])
    }

    func testAMarkWithNoValueIsPlacedByItsPointerAlone() {
        let marks = [arg(1): mark(nil)]
        XCTAssertEqual(PublishIntent.placePathMarks(marks, in: ["-y", "/anything"]).placed, [1: mark(nil)])
        let past = PublishIntent.placePathMarks(marks, in: ["-y"])
        XCTAssertEqual(past.placed, [:])
        XCTAssertEqual(past.unresolved, [:], "a pointer past the arguments marks nothing, as it always did")
    }

    func testExportPlacesAMovedMarkOnItsPathAndRefusesOneItCannotPlace() throws {
        let intent = PublishIntent(shareValues: [:], pathMarks: ["ledger": [arg(0): mark("/Users/d/ledger.js")]], hints: [:])
        let inserted: JSONValue = .object(["command": .string("node"),
                                           "args": .array([.string("--quiet"), .string("/Users/d/ledger.js")])])
        let doc = try CollectionDocument.export(name: "x", author: nil, origin: nil, exported: "2026-09-21T15:00:00Z",
                                                connectors: ["ledger": inserted], intent: intent)
        guard case .local(let l) = try XCTUnwrap(doc.connectors["ledger"]).launcher else { return XCTFail("ledger is not local") }
        XCTAssertEqual(l.args, ["--quiet", "${CC_NEEDS:path}"], "the flag that slid into its place travels as written, the path does not")

        let edited: JSONValue = .object(["command": .string("node"),
                                         "args": .array([.string("--quiet"), .string("/Users/d/ledger-v2.js")])])
        XCTAssertThrowsError(try CollectionDocument.export(name: "x", author: nil, origin: nil, exported: "2026-09-21T15:00:00Z",
                                                           connectors: ["ledger": edited, "other": inserted], intent: intent)) {
            XCTAssertEqual($0 as? PublishIntentError, .pathMarkMoved(connector: "ledger"))
        }
    }

    func testExportRefusesAnUnmarkedCopyOfAMarkedPath() throws {
        let intent = PublishIntent(shareValues: ["ledger": ["LEDGER"]], pathMarks: ["ledger": [arg(0): mark("/Users/d/ledger.js")]], hints: [:])
        // The field is named as the editor shows it, since each of these opens in the local form.
        let copies: [(field: String, config: JSONValue)] = [
            (FieldName.argument(2), .object(["command": .string("node"),
                                             "args": .array([.string("/Users/d/ledger.js"), .string("/Users/d/ledger.js")])])),
            (FieldName.command, .object(["command": .string("/Users/d/ledger.js"), "args": .array([.string("/Users/d/ledger.js")])])),
            (FieldName.envValue("LEDGER"), .object(["command": .string("node"), "args": .array([.string("/Users/d/ledger.js")]),
                                                    "env": .object(["LEDGER": .string("/Users/d/ledger.js")])])),
        ]
        for copy in copies {
            XCTAssertThrowsError(try CollectionDocument.export(name: "x", author: nil, origin: nil, exported: "2026-09-21T15:00:00Z",
                                                               connectors: ["ledger": copy.config], intent: intent), copy.field) {
                XCTAssertEqual($0 as? PublishIntentError, .keptPathCarried(connector: "ledger", field: copy.field),
                               "a copy is not a mark that moved: the refusal says where the copy sits")
            }
        }
        // An environment value nobody ticked to share stays here as a hint, so it copies nothing.
        let stripped: JSONValue = .object(["command": .string("node"), "args": .array([.string("/Users/d/ledger.js")]),
                                           "env": .object(["OTHER": .string("/Users/d/ledger.js")])])
        XCTAssertNoThrow(try CollectionDocument.export(name: "x", author: nil, origin: nil, exported: "2026-09-21T15:00:00Z",
                                                       connectors: ["ledger": stripped], intent: intent))
    }

    func testAKeptValueCountsAsTheWholeStringOrAsAPathOfItsOwn() {
        XCTAssertTrue(KeptValue.holds("/Users/d/a", "/Users/d/a"))
        XCTAssertTrue(KeptValue.holds("--config=/Users/d/a/b.json", "/Users/d/a"), "after =, before a separator")
        XCTAssertTrue(KeptValue.holds("run /Users/d/a now", "/Users/d/a"))
        XCTAssertTrue(KeptValue.holds(#"{"dir":"\/Users\/d\/a"}"#, "/Users/d/a"), "as a JSON blob spells it")
        XCTAssertTrue(KeptValue.holds(#"{"dir":"C:\\Users\\d"}"#, #"C:\Users\d"#))
        XCTAssertFalse(KeptValue.holds("/Users/d/a-tools/x.js", "/Users/d/a"), "a sibling that begins with its name")
        XCTAssertFalse(KeptValue.holds("/home/Users/d/a", "/Users/d/a"), "a longer path that merely ends in it")
        for separated in ["/Users/d/a:/opt/lib", "/Users/d/a;/opt/lib", "x,/Users/d/a", "(/Users/d/a)", "'/Users/d/a'", "/Users/d/a\\x"] {
            XCTAssertTrue(KeptValue.holds(separated, "/Users/d/a"), separated)
        }
        for continued in ["/Users/d/a.bak", "/Users/d/a_old", "/Users/d/aé", "/Users/d/a2", "é/Users/d/a"] {
            XCTAssertFalse(KeptValue.holds(continued, "/Users/d/a"), continued)
        }
        XCTAssertTrue(KeptValue.holds(".", "."))
        XCTAssertFalse(KeptValue.holds("./start.sh", "."), "a relative value counts only as the whole string")
        XCTAssertFalse(KeptValue.holds("https://mcp.notion.com/mcp", "."))
        XCTAssertFalse(KeptValue.holds("anything", ""))
    }

    func testAKeptValueMatchesInEitherUnicodeNormalization() {
        let composed = "/Users/d/caf\u{E9}/x.js", decomposed = "/Users/d/cafe\u{301}/x.js"
        XCTAssertTrue(KeptValue.holds(decomposed, composed))
        XCTAssertTrue(KeptValue.holds("--dir=" + composed, decomposed))
        XCTAssertEqual(PublishIntent.placePathMarks([arg(0): mark(composed)], in: [decomposed]).placed, [0: mark(composed)])
    }

    func testFindingsNameTheConnectorAndTheFieldEveryPlaceAValueSits() throws {
        let document = CollectionDocument(name: "x", author: nil, origin: nil, exported: "2026-09-21T15:00:00Z", connectors: [
            "ledger": .init(launcher: .local(.init(command: "node", args: ["--root=/Users/d/a", "x.js"], platform: .current)),
                            env: ["LOG": .value("/Users/d/a/log"), "TOKEN": .hint("like /Users/d/a")],
                            needs: ["server_path": "your clone, not /Users/d/a"],
                            additional: ["cwd": .string("/Users/d/a")]),
            "remote": .init(launcher: .remote(.init(url: "https://mcp.example.com/", auth: .automatic, package: "mcp-remote",
                                                    extraArgs: ["--config", "/Users/d/a/r.json"]))),
            "clean": .init(launcher: .local(.init(command: "node", args: ["/Users/d/a-tools/x.js"], platform: .current))),
        ])
        XCTAssertEqual(document.findings(of: ["/Users/d/a"]).map { "\($0.connector) \($0.field)" }, [
            "ledger additional.cwd", "ledger env.LOG.value", "ledger env.TOKEN.hint", "ledger local.args[0]",
            "ledger needs.server_path.hint", "remote remote.extraArgs[1]",
        ])
        XCTAssertEqual(document.findings(of: ["."]), [], "a short value is found only where a string is exactly it")
        XCTAssertEqual(document.findings(of: [""]), [])
    }

    func testReplacingWritesOverEveryOccurrenceAndOnlyThose() {
        XCTAssertEqual(KeptValue.replacing("/share", in: "/share:/share-tools:/share", with: "${COLLECTION_DIR}"),
                       "${COLLECTION_DIR}:/share-tools:${COLLECTION_DIR}")
        XCTAssertEqual(KeptValue.replacing("/share", in: #"{"root":"\/share\/x"}"#, with: "${COLLECTION_DIR}"),
                       #"{"root":"${COLLECTION_DIR}\/x"}"#, "an escaped path inside a JSON blob is replaced whole")
        XCTAssertEqual(KeptValue.replacing(".", in: "./x", with: "T"), "./x", "a relative value only as the whole string")
    }

    func testUsingDirectoryTokenRewritesTheOnePlaceTheFieldNames() {
        let config: JSONValue = .object([
            "command": .string("/share/bin/tool"),
            "args": .array([.int(1), .string("--root"), .string("/share")]),
            "env": .object(["EXTRA": .string("/share:/opt")]),
            "cwd": .string("/share"),
            "nested": .object(["dirs": .array([.string("/share/a")])]),
        ])
        let token = Placeholder.directoryToken
        func rewritten(_ field: String) -> JSONValue? { CollectionDocument.usingDirectoryToken(in: config, field: field, folder: "/share") }
        XCTAssertEqual(rewritten("local.command"), config.replacing(at: JSONPointer(["command"]), with: .string("\(token)/bin/tool")))
        XCTAssertEqual(rewritten("local.args[1]"), config.replacing(at: JSONPointer(["args", "2"]), with: .string(token)),
                       "the document numbers only the string arguments")
        XCTAssertEqual(rewritten("env.EXTRA.value"), config.replacing(at: JSONPointer(["env", "EXTRA"]), with: .string("\(token):/opt")))
        XCTAssertEqual(rewritten("additional.cwd"), config.replacing(at: JSONPointer(["cwd"]), with: .string(token)))
        XCTAssertEqual(rewritten("additional.nested.dirs[0]"),
                       config.replacing(at: JSONPointer(["nested", "dirs", "0"]), with: .string("\(token)/a")))
        XCTAssertNil(rewritten("local.args[0]"), "a place that does not hold the folder")
        XCTAssertNil(rewritten("env.EXTRA.hint"), "a hint is the Publish sheet's own")

        let remote = RemotePattern.encode(RemoteConfig(url: "https://mcp.example.com/", auth: .automatic,
                                                       extraArgs: ["--config", "/share/r.json"], passthroughEnv: [:], package: "mcp-remote"))
        let fixed = CollectionDocument.usingDirectoryToken(in: remote, field: "remote.extraArgs[1]", folder: "/share")
        XCTAssertEqual(fixed.flatMap(RemotePattern.decode)?.extraArgs, ["--config", "\(token)/r.json"])

        // The command line carries a client id inside a JSON blob, which no field of it reads as
        // written: the sheet says so rather than reporting a rewrite it did not make.
        let oauth = RemotePattern.encode(RemoteConfig(url: "https://mcp.example.com/",
                                                      auth: .oauthClient(clientID: "/share", clientSecret: "s", scopes: ""),
                                                      extraArgs: [], passthroughEnv: [:], package: "mcp-remote"))
        XCTAssertNil(CollectionDocument.usingDirectoryToken(in: oauth, field: "remote.auth.clientId", folder: "/share"))
    }

    func testCopiesOfMarkedPathsAreListedWithTheirFields() {
        let intent = PublishIntent(shareValues: [:], pathMarks: ["ledger": [arg(0): mark("/Users/d/ledger.js")]], hints: [:])
        let config: JSONValue = .object(["command": .string("/Users/d/ledger.js"),
                                         "args": .array([.string("/Users/d/ledger.js"), .string("/Users/d/ledger.js")])])
        XCTAssertEqual(CollectionDocument.copiesOfMarkedPaths(in: ["ledger": config], intent: intent).map(\.field),
                       ["local.args[1]", "local.command"])
    }

    /// A character past U+FFFF sorts before U+FF5E by UTF-16 code unit, which is how C# orders, and
    /// after it by Unicode scalar, which is Swift's `<`: the refusal names the same one on both.
    func testARefusalNamesTheFirstConnectorInOrdinalOrder() {
        let lost = PublishIntent(shareValues: [:], pathMarks: [
            "\u{FF5E}": [arg(0): mark("/gone/one.js")],
            "\u{1F600}": [arg(0): mark("/gone/two.js")],
        ], hints: [:])
        let config: JSONValue = .object(["command": .string("node"), "args": .array([.string("x.js")])])
        XCTAssertThrowsError(try CollectionDocument.export(name: "x", author: nil, origin: nil, exported: "2026-09-21T15:00:00Z",
                                                           connectors: ["\u{FF5E}": config, "\u{1F600}": config], intent: lost)) {
            XCTAssertEqual($0 as? PublishIntentError, .pathMarkMoved(connector: "\u{1F600}"))
        }
        XCTAssertTrue("\u{1F600}".ordinallyPrecedes("\u{FF5E}"))
        XCTAssertFalse("\u{1F600}" < "\u{FF5E}", "Swift's own order is the other way round")
    }

    func testPlacedArgumentsAreTheTextsTheMarksReplace() {
        let intent = PublishIntent(shareValues: [:], pathMarks: [
            "ledger": [arg(0): mark("/Users/d/ledger.js")],
            "remote": [arg(0): mark("/Users/d/r.js")],
            "gone": [arg(0): mark("/Users/d/g.js")],
        ], hints: [:])
        let connectors: [String: JSONValue] = [
            "ledger": .object(["command": .string("node"), "args": .array([.string("--quiet"), .string("/Users/d/ledger.js")])]),
            "remote": RemotePattern.encode(RemoteConfig(url: "https://mcp.example.com/", auth: .automatic, package: "mcp-remote")),
        ]
        XCTAssertEqual(intent.placedArguments(in: connectors), ["/Users/d/ledger.js"])
    }

    func testExportRefusesAMarkLeftOnARemoteConnector() {
        let remote = RemotePattern.encode(RemoteConfig(url: "https://mcp.example.com/", auth: .automatic,
                                                       extraArgs: ["/Users/d/ledger.js"], passthroughEnv: [:], package: "mcp-remote"))
        let intent = PublishIntent(shareValues: [:], pathMarks: ["ledger": [arg(0): mark("/Users/d/ledger.js")]], hints: [:])
        XCTAssertThrowsError(try CollectionDocument.export(name: "x", author: nil, origin: nil, exported: "2026-09-21T15:00:00Z",
                                                           connectors: ["ledger": remote], intent: intent)) {
            XCTAssertEqual($0 as? PublishIntentError, .pathMarkMoved(connector: "ledger"))
        }
    }

    func testARemoteConnectorKeepsItsAdditionalFieldsThroughExportAndRender() throws {
        let encoded = RemotePattern.encode(RemoteConfig(url: "https://mcp.example.com/", auth: .automatic, package: "mcp-remote"))
        guard case .object(var object) = encoded else { return XCTFail("encode did not produce an object") }
        object["type"] = .string("stdio")
        let config: JSONValue = .object(object)
        let doc = try CollectionDocument.export(name: "x", author: nil, origin: nil, exported: "2026-09-21T15:00:00Z",
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
        let doc = try CollectionDocument.export(name: "x", author: nil, origin: nil, exported: "2026-09-21T15:00:00Z",
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

import XCTest
@testable import ConnectorControlCore

final class RemoteAuthTests: XCTestCase {
    private let url = "https://x.dev/mcp"

    private func args(_ v: JSONValue) -> [String]? {
        guard case .object(let o) = v, case .array(let a)? = o["args"] else { return nil }
        return a.compactMap { if case .string(let s) = $0 { return s }; return nil }
    }

    private func env(_ v: JSONValue) -> [String: String] {
        guard case .object(let o) = v, case .object(let e)? = o["env"] else { return [:] }
        var out: [String: String] = [:]
        for (k, val) in e { if case .string(let s) = val { out[k] = s } }
        return out
    }

    // MARK: encode/decode round-trip, one per auth mode

    func testAutomaticRoundTrips() {
        let rc = RemoteConfig(url: url, auth: .automatic)
        let encoded = RemotePattern.encode(rc)
        XCTAssertEqual(args(encoded), ["-y", "mcp-remote", url])
        XCTAssertEqual(RemotePattern.decode(encoded), rc)
    }

    func testBearerRoundTrips() {
        let rc = RemoteConfig(url: url, auth: .bearer(token: "secret-tok"))
        let encoded = RemotePattern.encode(rc)
        XCTAssertEqual(args(encoded),
                       ["-y", "mcp-remote", url, "--header", "Authorization:${AUTH_HEADER}"])
        XCTAssertEqual(env(encoded), ["AUTH_HEADER": "Bearer secret-tok"])
        XCTAssertEqual(RemotePattern.decode(encoded), rc)
    }

    func testHeaderRoundTrips() {
        let rc = RemoteConfig(url: url, auth: .header(name: "X-API-Key", value: "k123"))
        let encoded = RemotePattern.encode(rc)
        XCTAssertEqual(args(encoded),
                       ["-y", "mcp-remote", url, "--header", "X-API-Key:${AUTH_HEADER}"])
        XCTAssertEqual(env(encoded), ["AUTH_HEADER": "k123"])
        XCTAssertEqual(RemotePattern.decode(encoded), rc)
    }

    func testOAuthClientRoundTrips() {
        let rc = RemoteConfig(url: url,
                               auth: .oauthClient(clientID: "cid", clientSecret: "csecret",
                                                   scopes: "read write"))
        let encoded = RemotePattern.encode(rc)
        XCTAssertEqual(args(encoded), [
            "-y", "mcp-remote", url,
            "--static-oauth-client-info", "{\"client_id\":\"cid\",\"client_secret\":\"csecret\"}",
            "--static-oauth-client-metadata", "{\"scope\":\"read write\"}",
        ])
        XCTAssertEqual(RemotePattern.decode(encoded), rc)
    }

    // MARK: specific decode scenarios

    func testDecodeBearerFromExplicitConfig() {
        let config = JSONValue.object([
            "command": .string("npx"),
            "args": .array([.string("-y"), .string("mcp-remote"), .string(url),
                             .string("--header"), .string("Authorization:${AUTH_HEADER}")]),
            "env": .object(["AUTH_HEADER": .string("Bearer abc")]),
        ])
        let decoded = RemotePattern.decode(config)
        XCTAssertEqual(decoded?.auth, .bearer(token: "abc"))
        XCTAssertEqual(decoded?.passthroughEnv, [:])
        // Re-encoding reproduces the same args/env (modulo key order, which
        // JSONValue.object equality already ignores).
        XCTAssertEqual(RemotePattern.encode(decoded!), config)
    }

    func testDecodeCustomHeaderFromExplicitConfig() {
        let config = JSONValue.object([
            "command": .string("npx"),
            "args": .array([.string("-y"), .string("mcp-remote"), .string(url),
                             .string("--header"), .string("X-API-Key:${AUTH_HEADER}")]),
            "env": .object(["AUTH_HEADER": .string("topsecret")]),
        ])
        let decoded = RemotePattern.decode(config)
        XCTAssertEqual(decoded?.auth, .header(name: "X-API-Key", value: "topsecret"))
        XCTAssertEqual(RemotePattern.encode(decoded!), config)
    }

    func testOAuthClientWithoutScopes() {
        let rc = RemoteConfig(url: url,
                               auth: .oauthClient(clientID: "cid", clientSecret: "", scopes: ""))
        let encoded = RemotePattern.encode(rc)
        XCTAssertEqual(args(encoded), [
            "-y", "mcp-remote", url,
            "--static-oauth-client-info", "{\"client_id\":\"cid\",\"client_secret\":\"\"}",
        ])
        XCTAssertEqual(RemotePattern.decode(encoded), rc)
    }

    // MARK: preservation

    func testBearerPlusExtraHeaderPreservesBoth() {
        // A bearer auth (our sentinel pattern) plus a hand-added literal header.
        // The bearer must decode as auth; the extra header must survive verbatim
        // as an extra arg — not clobber the bearer or steal its env var.
        let config = JSONValue.object([
            "command": .string("npx"),
            "args": .array([.string("-y"), .string("mcp-remote"),
                            .string("https://x.dev/mcp"),
                            .string("--header"), .string("Authorization:${AUTH_HEADER}"),
                            .string("--header"), .string("X-Tenant:acme")]),
            "env": .object(["AUTH_HEADER": .string("Bearer secret-tok")]),
        ])
        let decoded = RemotePattern.decode(config)
        XCTAssertEqual(decoded?.auth, .bearer(token: "secret-tok"))
        XCTAssertEqual(decoded?.extraArgs, ["--header", "X-Tenant:acme"])
        XCTAssertTrue(decoded?.passthroughEnv.isEmpty ?? false,
                      "the bearer's AUTH_HEADER is consumed; nothing else leaks")
        // Re-encode keeps both headers.
        let re = RemotePattern.encode(decoded!)
        let a = args(re) ?? []
        XCTAssertTrue(a.contains("Authorization:${AUTH_HEADER}"))
        XCTAssertTrue(a.contains("X-Tenant:acme"))
        XCTAssertEqual(env(re)["AUTH_HEADER"], "Bearer secret-tok")
    }

    func testUnrecognizedHeaderEnvVarIsPreservedNotFabricated() {
        // A header using a NON-sentinel env var must be left as an extra arg and
        // its env var kept in passthrough — never resolved into the auth slot.
        let config = JSONValue.object([
            "command": .string("npx"),
            "args": .array([.string("-y"), .string("mcp-remote"),
                            .string("https://x.dev/mcp"),
                            .string("--header"), .string("X-Key:${MY_KEY}")]),
            "env": .object(["MY_KEY": .string("abc")]),
        ])
        let decoded = RemotePattern.decode(config)
        XCTAssertEqual(decoded?.auth, .automatic)
        XCTAssertEqual(decoded?.extraArgs, ["--header", "X-Key:${MY_KEY}"])
        XCTAssertEqual(decoded?.passthroughEnv["MY_KEY"], "abc")
        XCTAssertEqual(RemotePattern.encode(decoded!), config)
    }

    func testExtraArgsPreserved() {
        let rc = RemoteConfig(url: url, auth: .automatic, extraArgs: ["--transport", "http-only"])
        let encoded = RemotePattern.encode(rc)
        XCTAssertEqual(args(encoded), ["-y", "mcp-remote", url, "--transport", "http-only"])
        XCTAssertEqual(RemotePattern.decode(encoded), rc)
    }

    func testPassthroughEnvSurvivesDecodeThenEncode() {
        let config = JSONValue.object([
            "command": .string("npx"),
            "args": .array([.string("-y"), .string("mcp-remote"), .string(url)]),
            "env": .object(["UNRELATED": .string("keep-me")]),
        ])
        let decoded = RemotePattern.decode(config)
        XCTAssertEqual(decoded?.passthroughEnv, ["UNRELATED": "keep-me"])
        XCTAssertEqual(decoded?.auth, .automatic)
        XCTAssertEqual(RemotePattern.encode(decoded!), config)
    }

    // MARK: versioned marker

    func testVersionedMarkerDecodes() {
        let config = JSONValue.object([
            "command": .string("npx"),
            "args": .array([.string("-y"), .string("mcp-remote@latest"), .string(url)]),
        ])
        let decoded = RemotePattern.decode(config)
        XCTAssertEqual(decoded?.url, url)
        XCTAssertEqual(decoded?.auth, .automatic)
    }

    // MARK: rejection

    func testDecodeReturnsNilForNonNpxCommand() {
        let config = JSONValue.object([
            "command": .string("node"),
            "args": .array([.string("-y"), .string("mcp-remote"), .string(url)]),
        ])
        XCTAssertNil(RemotePattern.decode(config))
    }

    func testDecodeReturnsNilForNonMarker() {
        let config = JSONValue.object([
            "command": .string("npx"),
            "args": .array([.string("-y"), .string("some-other-package"), .string(url)]),
        ])
        XCTAssertNil(RemotePattern.decode(config))
    }

    func testDecodeReturnsNilForMissingURL() {
        let config = JSONValue.object([
            "command": .string("npx"),
            "args": .array([.string("-y"), .string("mcp-remote")]),
        ])
        XCTAssertNil(RemotePattern.decode(config))
    }
}

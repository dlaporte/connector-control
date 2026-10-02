import ConnectorControlCore

/// The collection documents both test targets build on. The Core suite pins their bytes and
/// their rendering; the State suite subscribes to them on disk, so the sample lives here rather
/// than beside either one.
///
/// Mirror: windows/tests/ConnectorControl.Core.Tests/TestSupport/CollectionDocumentSamples.cs
public enum CollectionDocumentSamples {
    /// The document the design's example describes: two remote connectors, one local connector
    /// with a stripped and a shared env value, and one local connector whose path is a marker.
    public static let dataTeam = CollectionDocument(
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
}

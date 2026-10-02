namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>
/// The collection documents both suites build on. The Core tests pin their bytes and their
/// rendering; the State tests subscribe to them on disk, so the sample lives here rather than
/// beside either one.
///
/// Mirror: Tests/ConnectorControlTestSupport/CollectionDocumentSamples.swift
/// </summary>
public static class CollectionDocumentSamples
{
    /// <summary>
    /// The document the design's example describes: two remote connectors, one local connector
    /// with a stripped and a shared env value, and one local connector whose path is a marker.
    /// A fresh instance per read, so a test can build a variant of it without disturbing the next.
    /// </summary>
    public static CollectionDocument DataTeam => new(
        "Data team", "Acme Data Platform", "6f1c4a2e-1b8d-4b0e-9f0a-3c2d7e8a91e2",
        "2026-09-21T14:02:11Z",
        new Dictionary<string, CollectionDocument.Connector>
        {
            ["github"] = new(
                new CollectionDocument.Launcher.Remote("https://mcp.github.com/", CollectionDocument.Auth.Auto, "mcp-remote", []),
                new Dictionary<string, CollectionDocument.EnvValue>(),
                new Dictionary<string, string?>(),
                new Dictionary<string, JsonValue>()),
            ["notion"] = new(
                new CollectionDocument.Launcher.Remote("https://mcp.notion.com/", new CollectionDocument.Auth.Bearer(), "mcp-remote", []),
                new Dictionary<string, CollectionDocument.EnvValue>(),
                new Dictionary<string, string?> { ["token"] = "notion.so ▸ integrations" },
                new Dictionary<string, JsonValue>()),
            ["dbt"] = new(
                new CollectionDocument.Launcher.Local("npx", ["-y", "@dbt/mcp"], CollectionPlatform.Mac),
                new Dictionary<string, CollectionDocument.EnvValue>
                {
                    ["DBT_TOKEN"] = new CollectionDocument.EnvValue.Hint("cloud.getdbt.com ▸ API tokens"),
                    ["DBT_REGION"] = new CollectionDocument.EnvValue.Value("us"),
                },
                new Dictionary<string, string?>(),
                new Dictionary<string, JsonValue>()),
            ["ledger"] = new(
                new CollectionDocument.Launcher.Local("node", ["${CC_NEEDS:server_path}"], CollectionPlatform.Mac),
                new Dictionary<string, CollectionDocument.EnvValue>(),
                new Dictionary<string, string?> { ["server_path"] = "your ledger clone, then dist/index.js" },
                new Dictionary<string, JsonValue> { ["type"] = JsonValue.String("stdio") }),
        });
}

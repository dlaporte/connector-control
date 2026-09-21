using ConnectorControl.Core;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/CollectionDocumentTests.swift</summary>
public class CollectionDocumentTests
{
    private static CollectionDocument Sample => new(
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

    [Fact]
    public void EncodeDecodeRoundTrip()
    {
        var json = Sample.Encode();
        Assert.Equal(JsonValue.Int(1), json.ValueAt(new JsonPointer(["connectorControlCollection"])));
        Assert.Equal(Sample, CollectionDocument.Decode(json));
    }

    [Fact]
    public void GoldenBytesMatchTheFixture()
    {
        // Written once from the Mac's Foundation, then compared byte for byte there and here,
        // so both platforms write the same document. This side never regenerates it.
        var expected = File.ReadAllBytes(Fixtures.Path("collection.json"));
        Assert.Equal(expected, Sample.Serialize());
        Assert.Equal(Sample, CollectionDocument.Decode(expected));
    }

    [Fact]
    public void ANewerFormatIsRefused()
    {
        var json = Sample.Encode().Replacing(new JsonPointer(["connectorControlCollection"]), JsonValue.Int(2))!;
        var newer = Assert.Throws<CollectionDocumentException>(() => CollectionDocument.Decode(json));
        Assert.Equal(2, newer.NewerFormatVersion);
        var malformed = Assert.Throws<CollectionDocumentException>(
            () => CollectionDocument.Decode(JsonValue.Object(("name", JsonValue.String("x")))));
        Assert.Null(malformed.NewerFormatVersion);
    }

    [Fact]
    public void FileNameIsTheSlug()
    {
        Assert.Equal("data-team.json", Sample.FileName);
    }

    [Fact]
    public void RenderProducesThisPlatformsLaunchersWithMarkers()
    {
        var rendered = Sample.Render();
        Assert.Empty(rendered.Excluded);
        var notion = rendered.Connectors["notion"];
        // Platform-forced difference: Windows launches the bridge through cmd /c, the Mac runs npx directly.
        Assert.Equal(JsonValue.String("cmd"), notion.Config.ValueAt(new JsonPointer(["command"])));
        Assert.Equal(JsonValue.String("/c"), notion.Config.ValueAt(new JsonPointer(["args", "0"])));
        Assert.Equal(JsonValue.String("npx"), notion.Config.ValueAt(new JsonPointer(["args", "1"])));
        Assert.Equal(JsonValue.String("Bearer ${CC_NEEDS:token}"), notion.Config.ValueAt(new JsonPointer(["env", "AUTH_HEADER"])));
        Assert.Equal(new RenderedNeed("notion.so ▸ integrations", new JsonPointer(["env", "AUTH_HEADER"])), notion.Needs["token"]);
        Assert.Null(notion.AuthoredOn);
        var dbt = rendered.Connectors["dbt"];
        Assert.Equal(JsonValue.String("${CC_NEEDS:DBT_TOKEN}"), dbt.Config.ValueAt(new JsonPointer(["env", "DBT_TOKEN"])));
        Assert.Equal(JsonValue.String("us"), dbt.Config.ValueAt(new JsonPointer(["env", "DBT_REGION"])));
        Assert.Equal(new JsonPointer(["env", "DBT_TOKEN"]), dbt.Needs["DBT_TOKEN"].Pointer);
        Assert.Equal("cloud.getdbt.com ▸ API tokens", dbt.Needs["DBT_TOKEN"].Hint);
        // The document says the author was a Mac; rendering here does not change who wrote it.
        Assert.Equal(CollectionPlatform.Mac, dbt.AuthoredOn);
        var ledger = rendered.Connectors["ledger"];
        Assert.Equal(new RenderedNeed("your ledger clone, then dist/index.js", new JsonPointer(["args", "0"])), ledger.Needs["server_path"]);
        Assert.Equal(JsonValue.String("stdio"), ledger.Config.ValueAt(new JsonPointer(["type"])));
    }

    [Fact]
    public void ACmdUnsafeRemoteIsExcludedWithItsReason()
    {
        // The Mac has no cmd /c launcher, so this exclusion exists only here.
        var doc = new CollectionDocument("x", null, null, "2026-09-21T15:00:00Z", new Dictionary<string, CollectionDocument.Connector>
        {
            ["bad"] = new(new CollectionDocument.Launcher.Remote("https://h/mcp&calc", CollectionDocument.Auth.Auto, "mcp-remote", []),
                          new Dictionary<string, CollectionDocument.EnvValue>(), new Dictionary<string, string?>(), new Dictionary<string, JsonValue>()),
        });
        var rendered = doc.Render();
        Assert.Empty(rendered.Connectors);
        Assert.Equal(RemotePattern.CmdUnsafeReason(RemoteField.Url), rendered.Excluded["bad"]);
    }

    [Fact]
    public void ExportStripsSecretsSharesTickedValuesAndMarksPaths()
    {
        var notion = RemotePattern.Encode(new RemoteConfig("https://mcp.notion.com/", new RemoteAuth.Bearer("secret-1"),
                                                           RemoteLaunchStyle.CmdNpx, [], null, "mcp-remote"));
        var dbt = JsonValue.Object(
            ("command", JsonValue.String("npx")),
            ("args", JsonValue.Array([JsonValue.String("-y"), JsonValue.String("@dbt/mcp")])),
            ("env", JsonValue.Object(("DBT_TOKEN", JsonValue.String("tok")), ("DBT_REGION", JsonValue.String("us")))));
        var ledger = JsonValue.Object(
            ("command", JsonValue.String("node")),
            ("args", JsonValue.Array([JsonValue.String(@"C:\Users\d\ledger\dist\index.js")])),
            ("type", JsonValue.String("stdio")));
        var intent = new PublishIntent(
            new Dictionary<string, IReadOnlySet<string>> { ["dbt"] = new HashSet<string>(["DBT_REGION"], StringComparer.Ordinal) },
            new Dictionary<string, IReadOnlyDictionary<JsonPointer, PublishIntent.PathMark>>
            {
                ["ledger"] = new Dictionary<JsonPointer, PublishIntent.PathMark>
                {
                    [new JsonPointer(["args", "0"])] = new("server_path", "your ledger clone"),
                },
            },
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["dbt"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["DBT_TOKEN"] = "cloud.getdbt.com" },
                ["notion"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "notion.so" },
            });
        var doc = CollectionDocument.Export("Consulting", null, "o-1", "2026-09-21T15:00:00Z",
            new Dictionary<string, JsonValue> { ["notion"] = notion, ["dbt"] = dbt, ["ledger"] = ledger }, intent);
        var r = Assert.IsType<CollectionDocument.Launcher.Remote>(doc.Connectors["notion"].Launcher);
        Assert.Equal(new CollectionDocument.Auth.Bearer(), r.Auth);
        Assert.Equal(new Dictionary<string, string?> { ["token"] = "notion.so" }, doc.Connectors["notion"].Needs);
        Assert.DoesNotContain("secret-1", doc.Encode().EditorText(), StringComparison.Ordinal);
        Assert.Equal(
            new Dictionary<string, CollectionDocument.EnvValue>
            {
                ["DBT_TOKEN"] = new CollectionDocument.EnvValue.Hint("cloud.getdbt.com"),
                ["DBT_REGION"] = new CollectionDocument.EnvValue.Value("us"),
            },
            doc.Connectors["dbt"].Env);
        var l = Assert.IsType<CollectionDocument.Launcher.Local>(doc.Connectors["ledger"].Launcher);
        Assert.Equal(["${CC_NEEDS:server_path}"], l.Args);
        // Export records the platform that wrote the local launcher, which here is this build's.
        Assert.Equal(CollectionPlatform.Windows, l.Platform);
        Assert.Equal(new Dictionary<string, string?> { ["server_path"] = "your ledger clone" }, doc.Connectors["ledger"].Needs);
        Assert.Equal(new Dictionary<string, JsonValue> { ["type"] = JsonValue.String("stdio") }, doc.Connectors["ledger"].Additional);
    }

    [Fact]
    public void ExportKeepsAnUnfilledMarkerAsANeed()
    {
        var copy = JsonValue.Object(
            ("command", JsonValue.String("node")),
            ("args", JsonValue.Array([JsonValue.String("${CC_NEEDS:server_path}")])));
        var doc = CollectionDocument.Export("x", null, null, "2026-09-21T15:00:00Z",
            new Dictionary<string, JsonValue> { ["ledger"] = copy }, PublishIntent.None);
        Assert.Equal(new Dictionary<string, string?> { ["server_path"] = null }, doc.Connectors["ledger"].Needs);
    }

    [Fact]
    public void ARemoteConnectorKeepsItsAdditionalFieldsThroughExportAndRender()
    {
        var encoded = RemotePattern.Encode(new RemoteConfig("https://mcp.example.com/", RemoteAuth.Auto, RemoteLaunchStyle.Npx, package: "mcp-remote"));
        var props = new Dictionary<string, JsonValue>(encoded.ObjectProperties, StringComparer.Ordinal) { ["type"] = JsonValue.String("stdio") };
        var config = JsonValue.Object(props);
        var doc = CollectionDocument.Export("x", null, null, "2026-09-21T15:00:00Z",
            new Dictionary<string, JsonValue> { ["svc"] = config }, PublishIntent.None);
        Assert.Equal(new Dictionary<string, JsonValue> { ["type"] = JsonValue.String("stdio") }, doc.Connectors["svc"].Additional);
        var rendered = doc.Render().Connectors["svc"];
        Assert.Equal(JsonValue.String("stdio"), rendered.Config.ValueAt(new JsonPointer(["type"])));
        // Windows always renders through cmd /c, so the launcher command is "cmd", not "npx".
        Assert.Equal(JsonValue.String("cmd"), rendered.Config.ValueAt(new JsonPointer(["command"])));
    }

    [Fact]
    public void RenderMarksTheHeaderValueAndClientSecret()
    {
        var doc = new CollectionDocument("x", null, null, "2026-09-21T15:00:00Z", new Dictionary<string, CollectionDocument.Connector>
        {
            ["svc-header"] = new(
                new CollectionDocument.Launcher.Remote("https://mcp.example.com/", new CollectionDocument.Auth.Header("X-Api-Key"), "mcp-remote", []),
                new Dictionary<string, CollectionDocument.EnvValue>(),
                new Dictionary<string, string?> { ["header_value"] = "vendor dashboard ▸ API keys" },
                new Dictionary<string, JsonValue>()),
            ["svc-oauth"] = new(
                new CollectionDocument.Launcher.Remote("https://mcp.example.com/", new CollectionDocument.Auth.OAuthClient("id-1", "read write"), "mcp-remote", []),
                new Dictionary<string, CollectionDocument.EnvValue>(),
                new Dictionary<string, string?> { ["client_secret"] = "vendor dashboard ▸ OAuth apps" },
                new Dictionary<string, JsonValue>()),
        });
        var rendered = doc.Render();
        var header = rendered.Connectors["svc-header"];
        Assert.Equal(JsonValue.String("${CC_NEEDS:header_value}"), header.Config.ValueAt(new JsonPointer(["env", "AUTH_HEADER"])));
        // The cmd /c launcher prepends two args ("/c", "npx") ahead of the Mac's, shifting every index by two.
        Assert.Equal(JsonValue.String("X-Api-Key:${AUTH_HEADER}"), header.Config.ValueAt(new JsonPointer(["args", "6"])));
        Assert.Equal(new RenderedNeed("vendor dashboard ▸ API keys", new JsonPointer(["env", "AUTH_HEADER"])), header.Needs["header_value"]);

        var oauth = rendered.Connectors["svc-oauth"];
        var blob = oauth.Config.ValueAt(new JsonPointer(["args", "6"]));
        Assert.Contains("${CC_NEEDS:client_secret}", blob!.StringValue, StringComparison.Ordinal);
        Assert.Contains("\"id-1\"", blob.StringValue, StringComparison.Ordinal);
        Assert.Equal(new RenderedNeed("vendor dashboard ▸ OAuth apps", new JsonPointer(["args", "6"])), oauth.Needs["client_secret"]);
    }

    [Fact]
    public void ExportKeepsTheAuthKindAndNonSecretFieldsForEveryKind()
    {
        var automatic = RemotePattern.Encode(new RemoteConfig("https://mcp.example.com/", RemoteAuth.Auto, RemoteLaunchStyle.Npx, package: "mcp-remote"));
        var bearer = RemotePattern.Encode(new RemoteConfig("https://mcp.example.com/", new RemoteAuth.Bearer("secret-bearer"), RemoteLaunchStyle.Npx, package: "mcp-remote"));
        var header = RemotePattern.Encode(new RemoteConfig("https://mcp.example.com/", new RemoteAuth.Header("X-Api-Key", "secret-header"), RemoteLaunchStyle.Npx, package: "mcp-remote"));
        var oauth = RemotePattern.Encode(new RemoteConfig("https://mcp.example.com/", new RemoteAuth.OAuthClient("id-1", "secret-oauth", "read write"), RemoteLaunchStyle.Npx, package: "mcp-remote"));
        var doc = CollectionDocument.Export("x", null, null, "2026-09-21T15:00:00Z",
            new Dictionary<string, JsonValue> { ["automatic"] = automatic, ["bearer"] = bearer, ["header"] = header, ["oauth"] = oauth }, PublishIntent.None);
        var a = Assert.IsType<CollectionDocument.Launcher.Remote>(doc.Connectors["automatic"].Launcher);
        Assert.Equal(CollectionDocument.Auth.Auto, a.Auth);
        var b = Assert.IsType<CollectionDocument.Launcher.Remote>(doc.Connectors["bearer"].Launcher);
        Assert.Equal(new CollectionDocument.Auth.Bearer(), b.Auth);
        var h = Assert.IsType<CollectionDocument.Launcher.Remote>(doc.Connectors["header"].Launcher);
        Assert.Equal(new CollectionDocument.Auth.Header("X-Api-Key"), h.Auth);
        var o = Assert.IsType<CollectionDocument.Launcher.Remote>(doc.Connectors["oauth"].Launcher);
        Assert.Equal(new CollectionDocument.Auth.OAuthClient("id-1", "read write"), o.Auth);
        var serialized = doc.Encode().EditorText();
        foreach (var secret in new[] { "secret-bearer", "secret-header", "secret-oauth" })
        {
            Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CredentialWarningsNameThePosition()
    {
        var config = JsonValue.Object(
            ("command", JsonValue.String("npx")),
            ("args", JsonValue.Array([JsonValue.String("-y"), JsonValue.String("tax-mcp"), JsonValue.String("--key"), JsonValue.String("sk-live-9f3a")])));
        Assert.Equal(["args[3] looks like a credential"], CollectionDocument.CredentialWarnings(config, new HashSet<string>(StringComparer.Ordinal)));
    }

    [Fact]
    public void CredentialWarningsCoverHeaderPairsAndSharedValues()
    {
        var config = JsonValue.Object(
            ("command", JsonValue.String("npx")),
            ("args", JsonValue.Array([JsonValue.String("--header"), JsonValue.String("X-Key: sk-live-1")])),
            ("env", JsonValue.Object(("API_TOKEN", JsonValue.String("ghp_shared123")), ("OTHER", JsonValue.String("sk-live-should-be-ignored")))));
        // "X-Key: sk-live-1" has a space, so the whole string is never flagged; the part after
        // ": " is. OTHER isn't in sharedEnv, so its credential-shaped value is never scanned.
        Assert.Equal(
            ["args[1] looks like a credential", "env.API_TOKEN looks like a credential"],
            CollectionDocument.CredentialWarnings(config, new HashSet<string>(StringComparer.Ordinal) { "API_TOKEN" }));
    }
}

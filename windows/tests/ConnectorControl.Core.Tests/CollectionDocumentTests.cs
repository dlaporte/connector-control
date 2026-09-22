using ConnectorControl.Core;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/CollectionDocumentTests.swift</summary>
public class CollectionDocumentTests
{
    /// <summary>Shared with the State tests, which subscribe to this document on disk.</summary>
    private static CollectionDocument Sample => CollectionDocumentSamples.DataTeam;

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
                    [new JsonPointer(["args", "0"])] = new("server_path", "your ledger clone", @"C:\Users\d\ledger\dist\index.js"),
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

    // MARK: path marks follow their argument

    private static PublishIntent.PathMark Mark(string? value, string name = "path") => new(name, null, value);

    private static JsonPointer Arg(int index) =>
        new(["args", index.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

    private static Dictionary<JsonPointer, PublishIntent.PathMark> Marks(params (int Index, PublishIntent.PathMark Mark)[] marks) =>
        marks.ToDictionary(m => Arg(m.Index), m => m.Mark);

    private static PublishIntent LedgerIntent(PublishIntent.PathMark mark) => new(
        [],
        [new("ledger", Marks((0, mark)))],
        []);

    private static JsonValue Local(params string[] args) => JsonValue.Object(
        ("command", JsonValue.String("node")),
        ("args", JsonValue.Array(args.Select(JsonValue.String))));

    [Fact]
    public void AMarkStaysOnItsArgumentOrFollowsItsValue()
    {
        var marks = Marks((1, Mark("/Users/d/srv.js")));
        // Where it was marked.
        Assert.Equal(new Dictionary<int, PublishIntent.PathMark> { [1] = Mark("/Users/d/srv.js") },
            PublishIntent.PlacePathMarks(marks, ["-y", "/Users/d/srv.js"]).Placed);
        // An argument inserted above: the mark follows the path.
        Assert.Equal(new Dictionary<int, PublishIntent.PathMark> { [2] = Mark("/Users/d/srv.js") },
            PublishIntent.PlacePathMarks(marks, ["--quiet", "-y", "/Users/d/srv.js"]).Placed);
        // One removed above.
        Assert.Equal(new Dictionary<int, PublishIntent.PathMark> { [0] = Mark("/Users/d/srv.js") },
            PublishIntent.PlacePathMarks(marks, ["/Users/d/srv.js"]).Placed);
        // Reordered.
        var reordered = PublishIntent.PlacePathMarks(marks, ["/Users/d/srv.js", "-y"]);
        Assert.Equal(new Dictionary<int, PublishIntent.PathMark> { [0] = Mark("/Users/d/srv.js") }, reordered.Placed);
        Assert.Empty(reordered.Unresolved);
        // Still where it was marked, so a second copy elsewhere does not make it ambiguous.
        Assert.Equal(new Dictionary<int, PublishIntent.PathMark> { [1] = Mark("/Users/d/srv.js") },
            PublishIntent.PlacePathMarks(marks, ["/Users/d/srv.js", "/Users/d/srv.js"]).Placed);
    }

    [Fact]
    public void AMarkThatFindsNoArgumentIsUnresolved()
    {
        var marks = Marks((1, Mark("/Users/d/srv.js")));
        // Its value edited away.
        var edited = PublishIntent.PlacePathMarks(marks, ["-y", "/Users/d/other.js"]);
        Assert.Empty(edited.Placed);
        Assert.Equal(marks, edited.Unresolved);
        // Moved, and held by two arguments: which one it was is anybody's guess.
        var twice = PublishIntent.PlacePathMarks(marks, ["/Users/d/srv.js", "-y", "/Users/d/srv.js"]);
        Assert.Empty(twice.Placed);
        Assert.Equal(marks, twice.Unresolved);
    }

    [Fact]
    public void TwoMarksCannotShareOneArgument()
    {
        // Both were made on the same path; with one copy edited away, the one left can carry only one.
        var marks = Marks((0, Mark("/p", "a")), (1, Mark("/p", "b")));
        var placement = PublishIntent.PlacePathMarks(marks, ["/p", "/q"]);
        Assert.Equal(new Dictionary<int, PublishIntent.PathMark> { [0] = Mark("/p", "a") }, placement.Placed);
        Assert.Equal(Marks((1, Mark("/p", "b"))), placement.Unresolved);
    }

    [Fact]
    public void AMarkWithNoValueIsPlacedByItsPointerAlone()
    {
        var marks = Marks((1, Mark(null)));
        Assert.Equal(new Dictionary<int, PublishIntent.PathMark> { [1] = Mark(null) },
            PublishIntent.PlacePathMarks(marks, ["-y", "/anything"]).Placed);
        var past = PublishIntent.PlacePathMarks(marks, ["-y"]);
        Assert.Empty(past.Placed);
        // A pointer past the arguments marks nothing, as it always did.
        Assert.Empty(past.Unresolved);
    }

    [Fact]
    public void ExportPlacesAMovedMarkOnItsPathAndRefusesOneItCannotPlace()
    {
        var intent = LedgerIntent(Mark("/Users/d/ledger.js"));
        var inserted = Local("--quiet", "/Users/d/ledger.js");
        var doc = CollectionDocument.Export("x", null, null, "2026-09-21T15:00:00Z",
            new Dictionary<string, JsonValue> { ["ledger"] = inserted }, intent);
        var l = Assert.IsType<CollectionDocument.Launcher.Local>(doc.Connectors["ledger"].Launcher);
        // The flag that slid into its place travels as written, the path does not.
        Assert.Equal(["--quiet", "${CC_NEEDS:path}"], l.Args);

        var edited = Local("--quiet", "/Users/d/ledger-v2.js");
        var refused = Assert.Throws<PathMarkMovedException>(() => CollectionDocument.Export("x", null, null, "2026-09-21T15:00:00Z",
            new Dictionary<string, JsonValue> { ["ledger"] = edited, ["other"] = inserted }, intent));
        Assert.Equal("ledger", refused.Connector);
    }

    [Fact]
    public void ExportRefusesAnUnmarkedCopyOfAMarkedPath()
    {
        var intent = new PublishIntent(
            [new("ledger", new HashSet<string>(["LEDGER"], StringComparer.Ordinal))],
            [new("ledger", Marks((0, Mark("/Users/d/ledger.js"))))],
            []);
        var copies = new Dictionary<string, JsonValue>
        {
            ["another argument"] = Local("/Users/d/ledger.js", "/Users/d/ledger.js"),
            ["the command"] = JsonValue.Object(
                ("command", JsonValue.String("/Users/d/ledger.js")),
                ("args", JsonValue.Array([JsonValue.String("/Users/d/ledger.js")]))),
            ["an environment value"] = JsonValue.Object(
                ("command", JsonValue.String("node")),
                ("args", JsonValue.Array([JsonValue.String("/Users/d/ledger.js")])),
                ("env", JsonValue.Object(("LEDGER", JsonValue.String("/Users/d/ledger.js"))))),
        };
        foreach (var (place, config) in copies)
        {
            var refused = Assert.Throws<PathMarkMovedException>(() => CollectionDocument.Export("x", null, null, "2026-09-21T15:00:00Z",
                new Dictionary<string, JsonValue> { ["ledger"] = config }, intent));
            Assert.True(refused.Connector == "ledger", place);
        }
        // An environment value nobody ticked to share stays here as a hint, so it copies nothing.
        var stripped = JsonValue.Object(
            ("command", JsonValue.String("node")),
            ("args", JsonValue.Array([JsonValue.String("/Users/d/ledger.js")])),
            ("env", JsonValue.Object(("OTHER", JsonValue.String("/Users/d/ledger.js")))));
        CollectionDocument.Export("x", null, null, "2026-09-21T15:00:00Z",
            new Dictionary<string, JsonValue> { ["ledger"] = stripped }, intent);
    }

    [Fact]
    public void ConnectorCarryingFindsAValueAsWrittenOrAsJsonSpellsIt()
    {
        CollectionDocument.Connector LocalConnector(string arg) =>
            new(new CollectionDocument.Launcher.Local("node", [arg], CollectionPlatforms.Current));
        var document = new CollectionDocument("x", null, null, "2026-09-21T15:00:00Z", new Dictionary<string, CollectionDocument.Connector>
        {
            ["inside"] = LocalConnector("--config=/Users/d/a/b.json"),
            ["escaped"] = LocalConnector("""{"dir":"\/Users\/d\/c"}"""),
            ["clean"] = LocalConnector("x.js"),
        });
        Assert.Equal("inside", document.ConnectorCarrying(["/Users/d/a"]));   // held inside a longer string
        Assert.Equal("escaped", document.ConnectorCarrying(["/Users/d/c"]));  // held as a JSON blob spells it
        Assert.Null(document.ConnectorCarrying(["/Users/d/elsewhere"]));
        Assert.Null(document.ConnectorCarrying([""]));   // an empty value would match every string
        Assert.Null(document.ConnectorCarrying([]));
    }

    [Fact]
    public void ConnectorCarryingAFolderCountsItOnlyAsAFolderOfItsOwn()
    {
        static CollectionDocument Document(string arg) => new("x", null, null, "2026-09-21T15:00:00Z",
            new Dictionary<string, CollectionDocument.Connector>
            {
                ["c"] = new(new CollectionDocument.Launcher.Local("node", [arg], CollectionPlatforms.Current)),
            });
        Assert.Equal("c", Document(@"C:\Users\d\share\tools\x.js").ConnectorCarryingFolder(@"C:\Users\d\share"));
        Assert.Equal("c", Document(@"--root=C:\Users\d\share").ConnectorCarryingFolder(@"C:\Users\d\share"));
        // Inside a JSON blob as JSON spells it.
        Assert.Equal("c", Document("""{"root":"C:\\Users\\d\\share"}""").ConnectorCarryingFolder(@"C:\Users\d\share"));
        Assert.Null(Document(@"C:\Users\d\share-tools\x.js").ConnectorCarryingFolder(@"C:\Users\d\share"));
        Assert.Null(Document("${COLLECTION_DIR}/tools/x.js").ConnectorCarryingFolder(@"C:\Users\d\share"));
    }

    /// <summary>
    /// A character past U+FFFF sorts before U+FF5E by UTF-16 code unit, which is how this side
    /// orders, and after it by Unicode scalar, which is Swift's <c>&lt;</c>: the refusal names the
    /// same one on both.
    /// </summary>
    [Fact]
    public void ARefusalNamesTheFirstConnectorInOrdinalOrder()
    {
        var lost = new PublishIntent(
            [],
            [new("～", Marks((0, Mark("/gone/one.js")))), new("\U0001F600", Marks((0, Mark("/gone/two.js"))))],
            []);
        var refused = Assert.Throws<PathMarkMovedException>(() => CollectionDocument.Export("x", null, null, "2026-09-21T15:00:00Z",
            new Dictionary<string, JsonValue> { ["～"] = Local("x.js"), ["\U0001F600"] = Local("x.js") }, lost));
        Assert.Equal("\U0001F600", refused.Connector);
    }

    [Fact]
    public void PlacedArgumentsAreTheTextsTheMarksReplace()
    {
        var intent = new PublishIntent(
            [],
            [
                new("ledger", Marks((0, Mark("/Users/d/ledger.js")))),
                new("remote", Marks((0, Mark("/Users/d/r.js")))),
                new("gone", Marks((0, Mark("/Users/d/g.js")))),
            ],
            []);
        var connectors = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
        {
            ["ledger"] = Local("--quiet", "/Users/d/ledger.js"),
            ["remote"] = RemotePattern.Encode(new RemoteConfig("https://mcp.example.com/", RemoteAuth.Auto, RemoteLaunchStyle.Npx, package: "mcp-remote")),
        };
        Assert.Equal(["/Users/d/ledger.js"], intent.PlacedArguments(connectors));
    }

    [Fact]
    public void ExportRefusesAMarkLeftOnARemoteConnector()
    {
        var remote = RemotePattern.Encode(new RemoteConfig("https://mcp.example.com/", RemoteAuth.Auto, RemoteLaunchStyle.Npx,
            extraArgs: ["/Users/d/ledger.js"], package: "mcp-remote"));
        var refused = Assert.Throws<PathMarkMovedException>(() => CollectionDocument.Export("x", null, null, "2026-09-21T15:00:00Z",
            new Dictionary<string, JsonValue> { ["ledger"] = remote }, LedgerIntent(Mark("/Users/d/ledger.js"))));
        Assert.Equal("ledger", refused.Connector);
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

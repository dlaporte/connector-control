namespace ConnectorControl.Core.Tests;

public class RemoteAuthTests
{
    private const string Url = "https://x.dev/mcp";

    private static string[]? Args(JsonValue v) =>
        v["args"] is { Kind: JsonKind.Array } a ? a.ArrayItems.Where(i => i.Kind == JsonKind.String).Select(i => i.StringValue).ToArray() : null;

    private static Dictionary<string, string> Env(JsonValue v)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (v["env"] is { Kind: JsonKind.Object } e)
        {
            foreach (var (k, val) in e.ObjectProperties)
            {
                if (val.Kind == JsonKind.String) { result[k] = val.StringValue; }
            }
        }
        return result;
    }

    private static Dictionary<string, string> Pairs(params (string, string)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2, StringComparer.Ordinal);

    private static JsonValue NpxConfig(string[] args, Dictionary<string, string>? env = null)
    {
        var props = new List<(string, JsonValue)>
        {
            ("command", JsonValue.String("npx")),
            ("args", JsonValue.Array(args.Select(JsonValue.String))),
        };
        if (env is not null)
        {
            props.Add(("env", JsonValue.Object(env.Select(kv => new KeyValuePair<string, JsonValue>(kv.Key, JsonValue.String(kv.Value))))));
        }
        return JsonValue.Object(props.ToArray());
    }

    // encode/decode round-trip, one per auth mode

    [Fact]
    public void AutomaticRoundTrips()
    {
        var rc = new RemoteConfig(Url, RemoteAuth.Auto, RemoteLaunchStyle.Npx);
        var encoded = RemotePattern.Encode(rc);
        Assert.Equal(["-y", "mcp-remote", Url], Args(encoded)!);
        Assert.Equal(rc, RemotePattern.Decode(encoded));
    }

    [Fact]
    public void BearerRoundTrips()
    {
        var rc = new RemoteConfig(Url, new RemoteAuth.Bearer("secret-tok"), RemoteLaunchStyle.Npx);
        var encoded = RemotePattern.Encode(rc);
        Assert.Equal(["-y", "mcp-remote", Url, "--header", "Authorization:${AUTH_HEADER}"], Args(encoded)!);
        Assert.Equal(Pairs(("AUTH_HEADER", "Bearer secret-tok")), Env(encoded));
        Assert.Equal(rc, RemotePattern.Decode(encoded));
    }

    [Fact]
    public void HeaderRoundTrips()
    {
        var rc = new RemoteConfig(Url, new RemoteAuth.Header("X-API-Key", "k123"), RemoteLaunchStyle.Npx);
        var encoded = RemotePattern.Encode(rc);
        Assert.Equal(["-y", "mcp-remote", Url, "--header", "X-API-Key:${AUTH_HEADER}"], Args(encoded)!);
        Assert.Equal(Pairs(("AUTH_HEADER", "k123")), Env(encoded));
        Assert.Equal(rc, RemotePattern.Decode(encoded));
    }

    [Fact]
    public void OAuthClientRoundTrips()
    {
        var rc = new RemoteConfig(Url, new RemoteAuth.OAuthClient("cid", "csecret", "read write"), RemoteLaunchStyle.Npx);
        var encoded = RemotePattern.Encode(rc);
        Assert.Equal(
            ["-y", "mcp-remote", Url,
             "--static-oauth-client-info", "{\"client_id\":\"cid\",\"client_secret\":\"csecret\"}",
             "--static-oauth-client-metadata", "{\"scope\":\"read write\"}"],
            Args(encoded)!);
        Assert.Equal(rc, RemotePattern.Decode(encoded));
    }

    // specific decode scenarios

    [Fact]
    public void DecodeBearerFromExplicitConfig()
    {
        var config = NpxConfig(["-y", "mcp-remote", Url, "--header", "Authorization:${AUTH_HEADER}"], Pairs(("AUTH_HEADER", "Bearer abc")));
        var decoded = RemotePattern.Decode(config);
        Assert.NotNull(decoded);
        Assert.Equal(new RemoteAuth.Bearer("abc"), decoded.Auth);
        Assert.Empty(decoded.PassthroughEnv);
        Assert.Equal(config, RemotePattern.Encode(decoded));
    }

    [Fact]
    public void DecodeCustomHeaderFromExplicitConfig()
    {
        var config = NpxConfig(["-y", "mcp-remote", Url, "--header", "X-API-Key:${AUTH_HEADER}"], Pairs(("AUTH_HEADER", "topsecret")));
        var decoded = RemotePattern.Decode(config);
        Assert.NotNull(decoded);
        Assert.Equal(new RemoteAuth.Header("X-API-Key", "topsecret"), decoded.Auth);
        Assert.Equal(config, RemotePattern.Encode(decoded));
    }

    [Fact]
    public void OAuthClientWithoutScopes()
    {
        var rc = new RemoteConfig(Url, new RemoteAuth.OAuthClient("cid", "", ""), RemoteLaunchStyle.Npx);
        var encoded = RemotePattern.Encode(rc);
        Assert.Equal(["-y", "mcp-remote", Url, "--static-oauth-client-info", "{\"client_id\":\"cid\",\"client_secret\":\"\"}"], Args(encoded)!);
        Assert.Equal(rc, RemotePattern.Decode(encoded));
    }

    // preservation

    [Fact]
    public void BearerPlusExtraHeaderPreservesBoth()
    {
        var config = NpxConfig(
            ["-y", "mcp-remote", "https://x.dev/mcp", "--header", "Authorization:${AUTH_HEADER}", "--header", "X-Tenant:acme"],
            Pairs(("AUTH_HEADER", "Bearer secret-tok")));
        var decoded = RemotePattern.Decode(config);
        Assert.NotNull(decoded);
        Assert.Equal(new RemoteAuth.Bearer("secret-tok"), decoded.Auth);
        Assert.Equal(["--header", "X-Tenant:acme"], decoded.ExtraArgs);
        Assert.Empty(decoded.PassthroughEnv);
        var re = RemotePattern.Encode(decoded);
        var a = Args(re) ?? [];
        Assert.Contains("Authorization:${AUTH_HEADER}", a);
        Assert.Contains("X-Tenant:acme", a);
        Assert.Equal("Bearer secret-tok", Env(re)["AUTH_HEADER"]);
    }

    [Fact]
    public void UnrecognizedHeaderEnvVarIsPreservedNotFabricated()
    {
        var config = NpxConfig(["-y", "mcp-remote", "https://x.dev/mcp", "--header", "X-Key:${MY_KEY}"], Pairs(("MY_KEY", "abc")));
        var decoded = RemotePattern.Decode(config);
        Assert.NotNull(decoded);
        Assert.Equal(RemoteAuth.Auto, decoded.Auth);
        Assert.Equal(["--header", "X-Key:${MY_KEY}"], decoded.ExtraArgs);
        Assert.Equal("abc", decoded.PassthroughEnv["MY_KEY"]);
        Assert.Equal(config, RemotePattern.Encode(decoded));
    }

    [Fact]
    public void ExtraArgsPreserved()
    {
        var rc = new RemoteConfig(Url, RemoteAuth.Auto, RemoteLaunchStyle.Npx, ["--transport", "http-only"]);
        var encoded = RemotePattern.Encode(rc);
        Assert.Equal(["-y", "mcp-remote", Url, "--transport", "http-only"], Args(encoded)!);
        Assert.Equal(rc, RemotePattern.Decode(encoded));
    }

    [Fact]
    public void PassthroughEnvSurvivesDecodeThenEncode()
    {
        var config = NpxConfig(["-y", "mcp-remote", Url], Pairs(("UNRELATED", "keep-me")));
        var decoded = RemotePattern.Decode(config);
        Assert.NotNull(decoded);
        Assert.Equal(Pairs(("UNRELATED", "keep-me")), decoded.PassthroughEnv);
        Assert.Equal(RemoteAuth.Auto, decoded.Auth);
        Assert.Equal(config, RemotePattern.Encode(decoded));
    }

    // versioned marker

    [Fact]
    public void VersionedMarkerDecodes()
    {
        var decoded = RemotePattern.Decode(NpxConfig(["-y", "mcp-remote@latest", Url]));
        Assert.Equal(Url, decoded?.Url);
        Assert.Equal(RemoteAuth.Auto, decoded?.Auth);
    }

    // rejection

    [Fact]
    public void DecodeReturnsNullForNonNpxCommand()
    {
        var config = JsonValue.Object(("command", JsonValue.String("node")),
            ("args", JsonValue.Array([JsonValue.String("-y"), JsonValue.String("mcp-remote"), JsonValue.String(Url)])));
        Assert.Null(RemotePattern.Decode(config));
    }

    [Fact]
    public void DecodeReturnsNullForNonMarker()
    {
        Assert.Null(RemotePattern.Decode(NpxConfig(["-y", "some-other-package", Url])));
    }

    [Fact]
    public void DecodeReturnsNullForMissingUrl()
    {
        Assert.Null(RemotePattern.Decode(NpxConfig(["-y", "mcp-remote"])));
    }
}

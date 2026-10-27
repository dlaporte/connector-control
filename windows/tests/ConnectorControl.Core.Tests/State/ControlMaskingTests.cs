using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.State;

/// <summary>Mirror: Tests/ConnectorControlStateTests/ControlMaskingTests.swift</summary>
public class ControlMaskingTests
{
    private const string Token = "ghp_0123456789abcdefghijklmnopqrstuvwxyzAB";

    private static JsonValue S(string s) => JsonValue.String(s);

    private static readonly JsonValue Github = JsonValue.Object(
        ("command", S("npx")),
        ("args", JsonValue.Array([S("-y"), S("@modelcontextprotocol/server-github")])),
        ("env", JsonValue.Object(("GITHUB_TOKEN", S(Token)), ("EMPTY", S("")), ("LATER", S("${CC_NEEDS:LATER}")))));

    private static JsonValue Splunk(string url, string token) =>
        RemotePattern.Encode(new RemoteConfig(url, new RemoteAuth.Bearer(token), RemoteLaunchStyle.Npx, [],
            new Dictionary<string, string>(), RemotePattern.DefaultPackage));

    [Fact]
    public void EnvironmentValuesReadAsSetOrNeedsValue()
    {
        Assert.Equal(
            JsonValue.Object(
                ("command", S("npx")),
                ("args", JsonValue.Array([S("-y"), S("@modelcontextprotocol/server-github")])),
                ("env", JsonValue.Object(("GITHUB_TOKEN", S("set")), ("EMPTY", S("needs value")), ("LATER", S("needs value"))))),
            ControlMasking.MaskedConfig(Github));
    }

    [Fact]
    public void ArgumentsThatReadAsCredentialsAreMasked()
    {
        Assert.Equal(
            ["--token", "•••", "--api-key=•••", "•••", "/Users/x/server.js", "${CC_NEEDS:server_path}",
             "${COLLECTION_DIR}/srv.js", "--port", "8080"],
            ControlMasking.Arguments(
                ["--token", "abc", "--api-key=sk-123", Token, "/Users/x/server.js", "${CC_NEEDS:server_path}",
                 "${COLLECTION_DIR}/srv.js", "--port", "8080"]));
    }

    [Fact]
    public void AUrlKeepsItsHostAndPathAndMasksItsPasswordAndQueryValues()
    {
        Assert.Equal("https://user:•••@example.com/mcp?api_key=•••&region=•••#top",
            ControlMasking.Url("https://user:hunter2@example.com/mcp?api_key=abc&region=us#top"));
        Assert.Equal("https://example.com/mcp?token=${CC_NEEDS:token}",
            ControlMasking.Url("https://example.com/mcp?token=${CC_NEEDS:token}"));
        Assert.Equal("https://example.com/mcp", ControlMasking.Url("https://example.com/mcp"));
        Assert.Equal("not a url", ControlMasking.Url("not a url"));
    }

    [Fact]
    public void AHeadersValueIsMaskedUnlessItRefersElsewhere()
    {
        Assert.Equal(
            ["-y", "mcp-remote", "https://x.example/mcp?k=•••",
             "--header", "Authorization:${AUTH_HEADER}", "--header", "X-Team: •••"],
            ControlMasking.Arguments(["-y", "mcp-remote", "https://x.example/mcp?k=v",
                "--header", "Authorization:${AUTH_HEADER}", "--header", "X-Team: blue"]));
    }

    [Fact]
    public void TheOAuthClientInfoAfterItsFlagIsMasked()
    {
        Assert.Equal(["--static-oauth-client-info", "•••"],
            ControlMasking.Arguments(["--static-oauth-client-info", "{\"client_id\":\"a\",\"client_secret\":\"b\"}"]));
    }

    [Fact]
    public void CodeLinesForALocalConnector()
    {
        Assert.Equal(
            [
                "command   npx",
                "args      -y @modelcontextprotocol/server-github",
                "env       EMPTY = needs your value",
                "          GITHUB_TOKEN = ••• (set)",
                "          LATER = needs your value",
            ],
            ControlMasking.CodeLines(Github));
    }

    [Fact]
    public void CodeLinesForARemoteConnectorNameItsBridgeUrlAndHeader()
    {
        Assert.Equal(
            [
                "type      remote, through mcp-remote",
                "url       https://harvard.splunkcloud.com/mcp?tenant=•••",
                "headers   Authorization = ••• (set)",
            ],
            ControlMasking.CodeLines(Splunk("https://harvard.splunkcloud.com/mcp?tenant=acme", "s3cr3t")));
    }

    [Fact]
    public void RevealShowsEverythingAsWritten()
    {
        Assert.Equal(Github, ControlMasking.MaskedConfig(Github, reveal: true));
        Assert.Contains($"          GITHUB_TOKEN = {Token}", ControlMasking.CodeLines(Github, reveal: true));
        Assert.Equal("https://u:p@h.example/?a=b", ControlMasking.Url("https://u:p@h.example/?a=b", reveal: true));
    }

    [Fact]
    public void ChangeLinesShowOnlyWhatDiffersAndAChangedSecretAsChanged()
    {
        var before = Splunk("https://a.example/mcp", "one");
        var after = Splunk("https://b.example/mcp", "two");
        Assert.Equal(
            [
                "url",
                "  − https://a.example/mcp",
                "  + https://b.example/mcp",
                "headers",
                "  − Authorization = ••• (set)",
                "  + Authorization = ••• (set)",
            ],
            ControlMasking.ChangeLines(before, after));
        Assert.Empty(ControlMasking.ChangeLines(before, before));
    }

    [Fact]
    public void NoSecretSurvivesMasking()
    {
        foreach (var config in new[] { Github, Splunk("https://h.example/mcp?key=k3y", "s3cr3t") })
        {
            var text = System.Text.Encoding.UTF8.GetString(ControlMasking.MaskedConfig(config).Serialize())
                + string.Concat(ControlMasking.CodeLines(config));
            foreach (var secret in new[] { Token, "s3cr3t", "k3y" })
            {
                Assert.DoesNotContain(secret, text);
            }
        }
    }

    [Fact]
    public void NoSecretSurvivesInArgumentsUrlsOrChanges()
    {
        var shown = string.Join("\n",
            ControlMasking.Arguments(["--token", "abc9", "--api-key=sk-123x", Token, "--header", "X-Team: blue7"])
                .Append(ControlMasking.Url("https://user:hunter2@example.com/mcp?api_key=abc9&region=us7#top"))
                .Concat(ControlMasking.ChangeLines(Splunk("https://a.example/mcp?k=old1", "one1"),
                    Splunk("https://a.example/mcp?k=new2", "two2"))));
        foreach (var secret in new[] { Token, "abc9", "sk-123x", "blue7", "hunter2", "us7", "old1", "new2", "one1", "two2" })
        {
            Assert.DoesNotContain(secret, shown);
        }
    }
}

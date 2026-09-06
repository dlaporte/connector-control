namespace ConnectorControl.Core.Tests;

public class PasteRecoveryTests
{
    private static string? Command(JsonValue? v) => v?["command"] is { Kind: JsonKind.String } s ? s.StringValue : null;

    private static string[]? Args(JsonValue? v) =>
        v?["args"] is { Kind: JsonKind.Array } a ? a.ArrayItems.Where(i => i.Kind == JsonKind.String).Select(i => i.StringValue).ToArray() : null;

    [Fact]
    public void BareStanzaWithTrailingBrace()
    {
        const string text = "  \"okta-mcp-server\": {\n    \"command\": \"/opt/homebrew/bin/uv\",\n    \"args\": [\"run\", \"--directory\", \"/x\", \"okta-mcp-server\"],\n    \"env\": { \"OKTA_ORG_URL\": \"https://example.okta.com\" }\n  }\n}\n";
        var r = PasteRecovery.Recover(text);
        Assert.Equal("okta-mcp-server", r?.Name);
        Assert.Equal("/opt/homebrew/bin/uv", Command(r?.Config));
    }

    [Fact]
    public void BareStanzaWithoutTrailingBrace()
    {
        var r = PasteRecovery.Recover("\"foo\": {\"command\": \"npx\"}");
        Assert.Equal("foo", r?.Name);
        Assert.Equal("npx", Command(r?.Config));
    }

    [Fact]
    public void McpServersWrapper()
    {
        var r = PasteRecovery.Recover("{\"mcpServers\": {\"bar\": {\"command\": \"uvx\"}}}");
        Assert.Equal("bar", r?.Name);
        Assert.Equal("uvx", Command(r?.Config));
    }

    [Fact]
    public void SingleEntryNameWrapper()
    {
        var r = PasteRecovery.Recover("{\"baz\": {\"command\": \"node\"}}");
        Assert.Equal("baz", r?.Name);
        Assert.Equal("node", Command(r?.Config));
    }

    [Fact]
    public void PlainConfigObjectIsNotRenamed()
    {
        var r = PasteRecovery.Recover("{\"command\": \"npx\", \"args\": [\"-y\", \"pkg\"]}");
        Assert.NotNull(r);
        Assert.Null(r.Name);
        Assert.Equal("npx", Command(r.Config));
    }

    [Fact]
    public void SingleKeyConfigNotUnwrapped()
    {
        var r = PasteRecovery.Recover("{\"command\": \"x\"}");
        Assert.NotNull(r);
        Assert.Null(r.Name);
        Assert.Equal("x", Command(r.Config));
    }

    [Fact]
    public void BracesInsideStringValuesAreIgnored()
    {
        var r = PasteRecovery.Recover("\"weird\": {\"command\": \"echo }}}\"}");
        Assert.Equal("weird", r?.Name);
        Assert.Equal("echo }}}", Command(r?.Config));
    }

    [Fact]
    public void MultiEntryMcpServersNotMisnamed()
    {
        var r = PasteRecovery.Recover("{\"mcpServers\": {\"a\": {\"command\":\"x\"}, \"b\": {\"command\":\"y\"}}}");
        Assert.NotEqual("mcpServers", r?.Name);
    }

    [Fact]
    public void GarbageReturnsNull()
    {
        Assert.Null(PasteRecovery.Recover("{{{ not json"));
        Assert.Null(PasteRecovery.Recover("   "));
    }

    [Fact]
    public void CurlyClosingQuoteIsNormalized()
    {
        var r = PasteRecovery.Recover("\"splunk\": {\"command\": \"npx\", \"args\": [\"-y\", \"mcp-remote@latest”]}");
        Assert.Equal("splunk", r?.Name);
        Assert.Equal("npx", Command(r?.Config));
        Assert.Equal(["-y", "mcp-remote@latest"], Args(r?.Config)!);
    }

    [Fact]
    public void ValidStraightQuoteConfigIsUnaffectedByNormalization()
    {
        var r = PasteRecovery.Recover("{\"command\": \"npx\", \"args\": [\"-y\", \"pkg\"]}");
        Assert.NotNull(r);
        Assert.Null(r.Name);
        Assert.Equal("npx", Command(r.Config));
        Assert.Equal(["-y", "pkg"], Args(r.Config)!);
    }
}

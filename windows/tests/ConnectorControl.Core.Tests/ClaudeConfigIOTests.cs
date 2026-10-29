using System.Text;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/ClaudeConfigIOTests.swift</summary>
public class ClaudeConfigIOTests : IDisposable
{
    private readonly TempDir dir = new("claude");
    private string Url => dir.File("claude_desktop_config.json");

    public void Dispose() => dir.Dispose();

    private void Write(string s) => File.WriteAllText(Url, s);

    private JsonValue Root() => JsonValue.Parse(File.ReadAllBytes(Url));

    private static readonly JsonValue Scoutbook = JsonValue.Object(
        ("command", JsonValue.String("npx")),
        ("args", JsonValue.Array([JsonValue.String("-y"), JsonValue.String("mcp-remote"), JsonValue.String("https://scoutbook.example.com/mcp")])));

    [Fact]
    public void ReadRealisticConfig()
    {
        Write(Fixtures.RealisticClaudeConfig);
        var servers = ClaudeConfigIO.ReadMcpServers(Url);
        Assert.Equal(new HashSet<string> { "scoutbook", "aws-mcp", "service-now" }, servers.Keys.ToHashSet());
        Assert.Equal(Scoutbook, servers["scoutbook"]);
    }

    [Fact]
    public void ReadMissingFileReturnsEmpty()
    {
        Assert.Empty(ClaudeConfigIO.ReadMcpServers(Url));
    }

    [Fact]
    public void ReadAbsentKeyReturnsEmpty()
    {
        Write("{\"preferences\": {}}");
        Assert.Empty(ClaudeConfigIO.ReadMcpServers(Url));
    }

    [Fact]
    public void ReadMalformedFileThrows()
    {
        Write("{oops");
        Assert.Throws<ClaudeConfigException>(() => ClaudeConfigIO.ReadMcpServers(Url));
    }

    [Fact]
    public void ReadNonObjectMcpServersThrows()
    {
        Write("{\"mcpServers\": \"surprise\"}");
        var ex = Assert.Throws<ClaudeConfigException>(() => ClaudeConfigIO.ReadMcpServers(Url));
        Assert.Equal("mcpServers is not a JSON object", ex.Detail);
    }

    [Fact]
    public void ReadNonObjectTopLevelThrows()
    {
        Write("[1, 2]");
        var ex = Assert.Throws<ClaudeConfigException>(() => ClaudeConfigIO.ReadMcpServers(Url));
        Assert.Equal("top level is not a JSON object", ex.Detail);
    }

    [Fact]
    public void WritePreservesEveryOtherKeyByValue()
    {
        Write(Fixtures.RealisticClaudeConfig);
        var before = Root();
        ClaudeConfigIO.Write(new Dictionary<string, JsonValue> { ["only-one"] = JsonValue.Object(("command", JsonValue.String("echo"))) }, Url);
        var after = Root();
        Assert.Equal(before.ObjectProperties.Keys.ToHashSet(), after.ObjectProperties.Keys.ToHashSet());
        foreach (var key in before.ObjectProperties.Keys.Where(k => k != "mcpServers"))
        {
            Assert.Equal(before[key], after[key]);
        }
        Assert.Equal(["only-one"], after["mcpServers"]!.ObjectProperties.Keys.ToArray());
    }

    [Fact]
    public void WriteToMissingFileCreatesIt()
    {
        ClaudeConfigIO.Write(new Dictionary<string, JsonValue> { ["a"] = JsonValue.Object(("command", JsonValue.String("x"))) }, Url);
        Assert.Equal(["mcpServers"], Root().ObjectProperties.Keys.ToArray());
    }

    [Fact]
    public void ReadEmptyFileReturnsEmptyServers()
    {
        Write("");
        Assert.Empty(ClaudeConfigIO.ReadMcpServers(Url));
    }

    [Fact]
    public void WriteToEmptyFileRecreatesConfig()
    {
        Write("");
        ClaudeConfigIO.Write(new Dictionary<string, JsonValue> { ["a"] = JsonValue.Object(("command", JsonValue.String("x"))) }, Url);
        Assert.Equal(["mcpServers"], Root().ObjectProperties.Keys.ToArray());
        Assert.Equal(["a"], ClaudeConfigIO.ReadMcpServers(Url).Keys.ToArray());
    }

    [Fact]
    public void WriteRefusesMalformedFile()
    {
        Write("{oops");
        Assert.Throws<ClaudeConfigException>(() => ClaudeConfigIO.Write(new Dictionary<string, JsonValue>(), Url));
        Assert.Equal("{oops", File.ReadAllText(Url));
    }

    [Fact]
    public void DisabledSubsetOmittedAndReadBack()
    {
        Write(Fixtures.RealisticClaudeConfig);
        var servers = new Dictionary<string, JsonValue>(ClaudeConfigIO.ReadMcpServers(Url));
        servers.Remove("aws-mcp");
        ClaudeConfigIO.Write(servers, Url);
        Assert.Equal(new HashSet<string> { "scoutbook", "service-now" }, ClaudeConfigIO.ReadMcpServers(Url).Keys.ToHashSet());
    }

    [Fact]
    public void WriteUsesAppleSerializationFormat()
    {
        Write("{\"preferences\": {\"sidebarMode\": \"x\"}, \"Zeta\": 1, \"alpha\": 0.5}");
        ClaudeConfigIO.Write(new Dictionary<string, JsonValue> { ["s"] = JsonValue.Object(("command", JsonValue.String("npx"))) }, Url);
        const string expected =
            "{\n  \"alpha\" : 0.5,\n  \"mcpServers\" : {\n    \"s\" : {\n      \"command\" : \"npx\"\n    }\n  },\n  \"preferences\" : {\n    \"sidebarMode\" : \"x\"\n  },\n  \"Zeta\" : 1\n}";
        Assert.Equal(expected, File.ReadAllText(Url));
    }

    /// <summary>A key the top level or mcpServers names twice — exactly, or in two spellings Unicode counts as
    /// the same text — is refused: this app's parser keeps one and Claude's may keep the other.</summary>
    [Fact]
    public void ARepeatedKeyIsRefusedExactlyOrAsAnEquivalentSpelling()
    {
        string[] repeated =
        [
            """{"x": 1, "x": 2}""",
            """{"mcpServers": {"a": {}, "a": {}}}""",
            "{\"mcpServers\": {\"caf\u00e9\": {}, \"cafe\u0301\": {}}}",
            """{"mcpServers": {"caf\u00e9": {}, "cafe\u0301": {}}}""",
            """{"mcpServers": {"\u0061": {}, "a": {}}}""",
            "\uFEFF{\"mcpServers\": {\"a\": {}, \"a\": {}}}",
        ];
        foreach (var json in repeated)
        {
            var refused = Assert.Throws<ClaudeConfigException>(() => ClaudeConfigIO.RequireUniqueKeys(Encoding.UTF8.GetBytes(json)));
            Assert.Equal(ClaudeConfigIO.RepeatedKeyError, refused.Detail);
        }
    }

    /// <summary>A config that is not UTF-8 is refused as malformed, as the Mac refuses it, and the key check
    /// never reads it as UTF-8: a brace inside one of its strings must not count as one.</summary>
    [Fact]
    public void AConfigThatIsNotUTF8IsRefusedAsMalformed()
    {
        Encoding[] encodings =
        [
            new UnicodeEncoding(bigEndian: false, byteOrderMark: false), new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
            new UnicodeEncoding(bigEndian: true, byteOrderMark: true), new UTF32Encoding(bigEndian: false, byteOrderMark: false),
            new UTF32Encoding(bigEndian: true, byteOrderMark: false), new UTF32Encoding(bigEndian: true, byteOrderMark: true),
        ];
        foreach (var json in new[] { """{"k":"∀}}"}""", """{"mcpServers":{"a":{},"a":{}}}""" })
        {
            foreach (var encoding in encodings)
            {
                var data = encoding.GetPreamble().Concat(encoding.GetBytes(json)).ToArray();
                Assert.Throws<ClaudeConfigException>(() => ClaudeConfigIO.ParseRoot(data));
                Assert.Throws<ClaudeConfigException>(() => ClaudeConfigIO.RequireUniqueKeys(data));
            }
        }
    }

    /// <summary>Below mcpServers' own names, and inside a string, nothing is refused: the file is written as it
    /// was parsed, so what Claude reads there is what this app read.</summary>
    [Fact]
    public void OnlyTheTopLevelAndTheServersNamesAreChecked()
    {
        string[] unique =
        [
            """{"mcpServers": {"a": {"env": {"K": "1", "K": "2"}}}}""",
            """{"mcpServers": {"a": {}, "b": {}}, "other": {"a": 1, "a": 2}}""",
            """{"k": "{\"a\":1,\"a\":2}", "mcpServers": {"a": [{"b": 1}, {"b": 2}]}, "m": ["x", "x"]}""",
            """{"mcpServers": "a", "other": {"mcpServers": {"a": 1, "a": 2}}}""",
            "",
        ];
        foreach (var json in unique)
        {
            ClaudeConfigIO.RequireUniqueKeys(Encoding.UTF8.GetBytes(json));
        }
    }

    /// <summary>A Claude config nested deeper than 64 is refused as malformed, as the Mac mirror refuses it.</summary>
    [Fact]
    public void AConfigNestedDeeperThan64IsRefusedAsMalformed()
    {
        static byte[] Config(int depth) =>
            Encoding.UTF8.GetBytes("{\"k\":" + new string('[', depth - 1) + new string(']', depth - 1) + "}");
        ClaudeConfigIO.ParseRoot(Config(64));
        Assert.Throws<ClaudeConfigException>(() => ClaudeConfigIO.ParseRoot(Config(65)));
        Assert.Throws<ClaudeConfigException>(() => ClaudeConfigIO.ParseRoot(Config(70)));
    }

    /// <summary>A Claude config with a trailing comma is refused as malformed, as Claude Desktop and the Mac mirror
    /// refuse it.</summary>
    [Fact]
    public void AConfigWithATrailingCommaIsRefusedAsMalformed()
    {
        foreach (var text in new[] { """{"mcpServers":{},}""", """{"mcpServers":{"a":{"command":"npx","args":["x",]}}}""" })
        {
            Assert.Throws<ClaudeConfigException>(() => ClaudeConfigIO.ParseRoot(Encoding.UTF8.GetBytes(text)));
        }
        ClaudeConfigIO.ParseRoot(Encoding.UTF8.GetBytes("""{"mcpServers":{"a":{"command":",}"}}}"""));
    }
}

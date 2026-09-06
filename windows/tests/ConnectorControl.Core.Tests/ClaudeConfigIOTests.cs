using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

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
}

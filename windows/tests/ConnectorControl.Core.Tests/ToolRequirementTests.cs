namespace ConnectorControl.Core.Tests;

public class ToolRequirementTests
{
    [Theory]
    [InlineData("npx", Tool.Npx)]
    [InlineData("node", Tool.Node)]
    [InlineData("uvx", Tool.Uvx)]
    [InlineData("uv", Tool.Uv)]
    [InlineData("NPX.CMD", Tool.Npx)]
    [InlineData("node.exe", Tool.Node)]
    [InlineData(" Uvx ", Tool.Uvx)]
    [InlineData("python", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("npx.cmd.exe", null)]   // only one suffix is stripped
    public void RecognisesTheFourToolsByBasename(string command, Tool? expected)
    {
        Assert.Equal(expected, ToolRequirement.RequiredTool(command, []));
    }

    [Fact]
    public void UnwrapsOneCmdSlashC()
    {
        Assert.Equal(Tool.Npx, ToolRequirement.RequiredTool("cmd", ["/c", "npx", "-y", "mcp-remote", "https://x.dev/mcp"]));
        Assert.Equal(Tool.Uvx, ToolRequirement.RequiredTool("cmd.exe", ["/C", "uvx"]));
        Assert.Null(ToolRequirement.RequiredTool("cmd", ["/c"]));
        Assert.Null(ToolRequirement.RequiredTool("cmd", ["/k", "npx"]));
        Assert.Null(ToolRequirement.RequiredTool("cmd", ["/c", "cmd", "/c", "npx"]));   // one level only
    }

    [Fact]
    public void LeavesPathsAlone()
    {
        Assert.Null(ToolRequirement.RequiredTool("/usr/local/bin/npx", []));
        Assert.Null(ToolRequirement.RequiredTool(@"C:\Program Files\nodejs\npx.cmd", []));
        Assert.Null(ToolRequirement.RequiredTool("./node", []));
        Assert.Null(ToolRequirement.RequiredTool("cmd", ["/c", "/opt/homebrew/bin/npx"]));
    }

    [Fact]
    public void ConfigOverloadReadsCommandAndArgs()
    {
        Assert.Equal(Tool.Npx, ToolRequirement.RequiredTool(RemotePattern.Make("https://x.dev/mcp", RemoteLaunchStyle.CmdNpx)));
        Assert.Equal(Tool.Npx, ToolRequirement.RequiredTool(RemotePattern.Make("https://x.dev/mcp", RemoteLaunchStyle.Npx)));
        Assert.Equal(Tool.Node, ToolRequirement.RequiredTool(JsonValue.Object(("command", JsonValue.String("node")))));
        Assert.Null(ToolRequirement.RequiredTool(JsonValue.Object(("args", JsonValue.Array([JsonValue.String("npx")])))));
        // a non-string arg empties the args
        Assert.Null(ToolRequirement.RequiredTool(JsonValue.Object(("command", JsonValue.String("cmd")), ("args", JsonValue.Array([JsonValue.String("/c"), JsonValue.Int(42)])))));
        Assert.Null(ToolRequirement.RequiredTool(JsonValue.String("npx")));
    }

    [Fact]
    public void RequiredToolsAcrossConfigsIsDedupedAndOrdered()
    {
        static JsonValue Cmd(string command, params string[] args) => JsonValue.Object(
            ("command", JsonValue.String(command)),
            ("args", JsonValue.Array(args.Select(JsonValue.String))));

        JsonValue[] configs =
        [
            Cmd("npx", "-y", "mcp-remote", "https://a.dev/mcp"),
            Cmd("cmd", "/c", "npx", "-y", "mcp-remote", "https://b.dev/mcp"),   // the same tool, wrapped
            Cmd("uvx", "some-server"),
            Cmd(@"C:\Program Files\nodejs\node.exe"),                            // a path: no tool
            Cmd("python"),                                                       // not one of the four
            JsonValue.String("not an object"),
        ];
        Assert.Equal([Tool.Npx, Tool.Uvx], ToolRequirement.RequiredTools(configs).ToArray());
        Assert.Empty(ToolRequirement.RequiredTools([]));
        Assert.Empty(ToolRequirement.RequiredTools([Cmd("python")]));
        // ToolInfo.All order (npx, node, uvx, uv), not the configs' order:
        Assert.Equal([Tool.Node, Tool.Uv], ToolRequirement.RequiredTools([Cmd("uv"), Cmd("node")]).ToArray());
    }
}

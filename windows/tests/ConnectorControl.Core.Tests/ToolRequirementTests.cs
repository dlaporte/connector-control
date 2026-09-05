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
    [InlineData("npx.cmd.exe", null)]   // only one suffix is stripped
    public void RecognisesTheFourToolsByBasename(string command, Tool? expected)
    {
        Assert.Equal(expected, ToolRequirement.RequiredTool(command, []));
    }

    [Fact]
    public void CmdSlashCIsUnwrappedOnce()
    {
        Assert.Equal(Tool.Npx, ToolRequirement.RequiredTool("cmd", ["/c", "npx", "-y", "mcp-remote", "https://x.dev/mcp"]));
        Assert.Equal(Tool.Uvx, ToolRequirement.RequiredTool("cmd.exe", ["/C", "uvx"]));
        Assert.Null(ToolRequirement.RequiredTool("cmd", ["/c"]));
        Assert.Null(ToolRequirement.RequiredTool("cmd", ["/k", "npx"]));
        Assert.Null(ToolRequirement.RequiredTool("cmd", ["/c", "cmd", "/c", "npx"]));   // one level only
    }

    [Fact]
    public void PathsAreLeftAlone()
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
}

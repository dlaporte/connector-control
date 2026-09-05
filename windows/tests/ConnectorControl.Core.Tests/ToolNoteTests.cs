namespace ConnectorControl.Core.Tests;

public class ToolNoteTests
{
    [Fact]
    public void MissingToolNoteCarriesTheInstallLinkAndCommand()
    {
        var note = ToolNote.For(Tool.Npx, ToolStatus.NotFound);
        Assert.NotNull(note);
        Assert.Equal("npx wasn’t found, so Claude Desktop won’t be able to start this connector.", note.Text);
        Assert.Equal("Install Node.js", note.LinkTitle);
        Assert.Equal("https://nodejs.org/en/download", note.LinkUrl);
        Assert.Equal("winget install OpenJS.NodeJS.LTS", note.InstallCommand);
        var uv = ToolNote.For(Tool.Uv, ToolStatus.NotFound);
        Assert.NotNull(uv);
        Assert.Equal("uv wasn’t found, so Claude Desktop won’t be able to start this connector.", uv.Text);
        Assert.Equal("Install uv", uv.LinkTitle);
        Assert.Equal("https://docs.astral.sh/uv/getting-started/installation/", uv.LinkUrl);
        Assert.Equal("winget install astral-sh.uv", uv.InstallCommand);
        Assert.Null(ToolNote.For(Tool.Node, new ToolStatus(@"C:\Program Files\nodejs\node.exe", "22.11.0")));
        Assert.Null(ToolNote.For(Tool.Node, null));   // unknown is not a problem yet
    }

    [Fact]
    public void StatusTextForEachState()
    {
        Assert.Equal("Checking…", ToolNote.StatusText(null));
        Assert.Equal("10.9.2", ToolNote.StatusText(new ToolStatus(@"C:\x\npx.cmd", "10.9.2")));
        Assert.Equal("Found", ToolNote.StatusText(new ToolStatus(@"C:\x\npx.cmd", null)));
        Assert.Equal("Not found", ToolNote.StatusText(ToolStatus.NotFound));
    }

    [Fact]
    public void StringsAndOrderMatchTheSpec()
    {
        Assert.Equal([Tool.Npx, Tool.Node, Tool.Uvx, Tool.Uv], ToolInfo.All.ToArray());
        Assert.Equal(["npx", "node", "uvx", "uv"], ToolInfo.All.Select(ToolInfo.Name).ToArray());
        Assert.Equal(Tool.Npx, ToolInfo.Parse("NPX"));
        Assert.Null(ToolInfo.Parse("python"));
        Assert.Equal(ToolFamily.NodeJs, ToolInfo.Family(Tool.Npx));
        Assert.Equal(ToolFamily.NodeJs, ToolInfo.Family(Tool.Node));
        Assert.Equal(ToolFamily.Uv, ToolInfo.Family(Tool.Uvx));
        Assert.Equal(ToolFamily.Uv, ToolInfo.Family(Tool.Uv));
        Assert.Equal("or run", ToolNote.OrRun);
        Assert.Equal("Tools", ToolNote.SettingsHeader);
        Assert.Equal("Connectors that run through npx, node, uvx or uv need them installed where Claude Desktop can find them.", ToolNote.SettingsCaption);
        Assert.Equal(".COM;.EXE;.BAT;.CMD", ToolProbe.DefaultPathExt);
    }
}

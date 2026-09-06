using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void TempDirIsCreatedAndDeleted()
    {
        string path;
        using (var dir = new TempDir())
        {
            path = dir.Path;
            Assert.True(Directory.Exists(path));
        }
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void SharedFixtureIsCopiedToOutput()
    {
        Assert.Contains("\"mcpServers\"", Fixtures.RealisticClaudeConfig);
    }
}

using ConnectorControl.Core.Tests.TestSupport;
using Xunit;

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
}

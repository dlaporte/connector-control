namespace ConnectorControl.App.Tests;

public class SmokeTests
{
    [Fact]
    public void RunsOnlyOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
        }
        Assert.True(OperatingSystem.IsWindows());
    }
}

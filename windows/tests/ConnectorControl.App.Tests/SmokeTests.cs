namespace ConnectorControl.App.Tests;

public class SmokeTests
{
    [Fact]
    public void RunsOnlyOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.True(OperatingSystem.IsWindows());
    }
}

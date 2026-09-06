using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.State;

public class PathContextTests
{
    [Fact]
    public void LiveReadsTheProcessEnvironmentAndKnownFolders()
    {
        Environment.SetEnvironmentVariable("CONNECTOR_CONTROL_PLAN_PROBE", "yes");
        try
        {
            var live = PathContext.Live();
            Assert.Equal("yes", live.Environment["CONNECTOR_CONTROL_PLAN_PROBE"]);
            Assert.Equal(KnownFolders.Current(), live.Folders);
            Assert.IsType<RealPathProbe>(live.Probe);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONNECTOR_CONTROL_PLAN_PROBE", null);
        }
    }
}

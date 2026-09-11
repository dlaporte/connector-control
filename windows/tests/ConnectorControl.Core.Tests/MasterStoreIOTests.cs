using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

public class MasterStoreIOTests : IDisposable
{
    private readonly TempDir dir = new("masterstoreio");

    public void Dispose() => dir.Dispose();

    [Fact]
    public void SaveStoreReportsTheOutcome()
    {
        var store = new MasterStore([]);
        Assert.True(MasterStoreIO.Save(store, dir.File("mcps.json")).Protected);
    }
}

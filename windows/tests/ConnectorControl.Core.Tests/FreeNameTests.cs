namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/FreeNameTests.swift</summary>
public class FreeNameTests
{
    [Fact]
    public void AFreeNameIsKeptAsItIs()
    {
        Assert.Equal("Docs", FreeName.First("Docs", _ => false));
    }

    [Fact]
    public void ATakenNameCountsUpFromTwoWithASpace()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { "Docs", "Docs 2", "Docs 3" };
        Assert.Equal("Docs 4", FreeName.First("Docs", taken.Contains));
    }

    [Fact]
    public void AGapIsFilledFirst()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { "Docs", "Docs 3" };
        Assert.Equal("Docs 2", FreeName.First("Docs", taken.Contains));
    }
}

namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/SlugTests.swift</summary>
public class SlugTests
{
    [Fact]
    public void LowercasesAndHyphenatesRuns()
    {
        Assert.Equal("data-team", Slug.Make("Data team"));
        Assert.Equal("acme-consulting-2026", Slug.Make("  Acme -- Consulting (2026) "));
    }

    [Fact]
    public void DropsNonASCIIAndNeverStartsOrEndsWithAHyphen()
    {
        Assert.Equal("quipe-donn-es", Slug.Make("Équipe données"));
        Assert.Equal("collection", Slug.Make("---"));
        Assert.Equal("collection", Slug.Make(""));
    }

    /// <summary>C#-only half of a known difference (see <see cref="Slug.Make"/>): the Swift test pins the other half.</summary>
    [Fact]
    public void ALetterWhoseLowercaseExpandsSlugsAsWindowsLowersIt()
    {
        Assert.Equal("stanbul", Slug.Make("\u0130stanbul"));
    }
}

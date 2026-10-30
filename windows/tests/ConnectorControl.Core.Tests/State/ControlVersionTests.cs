using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.State;

/// <summary>Mirror: Tests/ConnectorControlStateTests/ControlVersionTests.swift</summary>
public class ControlVersionTests
{
    [Fact]
    public void TheSemanticPartOfAVersionAsTheAppsShowIt()
    {
        Assert.Equal("1.5.0", ControlVersion.Semantic("1.5.0"));
        // The Mac's build number goes.
        Assert.Equal("1.5.0", ControlVersion.Semantic("1.5.0 (10500)"));
        Assert.Equal("1.5.0-preview.3", ControlVersion.Semantic("1.5.0-preview.3 (10499.3)"));
        // .NET's source revision goes.
        Assert.Equal("1.5.0", ControlVersion.Semantic("1.5.0+1a2b3c4def"));
        Assert.Equal("1.5.0-preview.3", ControlVersion.Semantic("1.5.0-preview.3+1a2b3c4"));
        // A dash with nothing after it is no prerelease.
        Assert.Equal("1.5.0", ControlVersion.Semantic("1.5.0-"));
        Assert.Null(ControlVersion.Semantic("development build"));
        Assert.Null(ControlVersion.Semantic("1.5"));
        Assert.Null(ControlVersion.Semantic(""));
    }

    [Fact]
    public void PrecedenceFollowsSemanticVersioning()
    {
        Assert.True(ControlVersion.IsNewer("1.6.0", "1.5.0"));
        // Numbers, not text.
        Assert.True(ControlVersion.IsNewer("1.5.10", "1.5.9"));
        Assert.True(ControlVersion.IsNewer("2.0.0", "1.99.99"));
        // A release comes after its previews.
        Assert.True(ControlVersion.IsNewer("1.5.0", "1.5.0-preview.3"));
        Assert.True(ControlVersion.IsNewer("1.5.0-preview.10", "1.5.0-preview.9"));
        // Letters after numbers.
        Assert.True(ControlVersion.IsNewer("1.5.0-preview.a", "1.5.0-preview.9"));
        // More identifiers, the rest equal.
        Assert.True(ControlVersion.IsNewer("1.5.0-preview.3.1", "1.5.0-preview.3"));
        // Display forms compare by their version.
        Assert.True(ControlVersion.IsNewer("1.5.0 (10500)", "1.4.0"));
        Assert.False(ControlVersion.IsNewer("1.5.0", "1.5.0"));
        Assert.False(ControlVersion.IsNewer("1.5.0-preview.3", "1.5.0"));
        Assert.False(ControlVersion.IsNewer("development build", "1.5.0"));
        Assert.False(ControlVersion.IsNewer("1.6.0", "development build"));
        // A number past 64 bits is no version.
        Assert.False(ControlVersion.IsNewer("99999999999999999999.0.0", "1.0.0"));
        // "-0" is a word, and words come after numbers.
        Assert.True(ControlVersion.IsNewer("1.5.0-a.-0", "1.5.0-a.1"));
        // The same number.
        Assert.False(ControlVersion.IsNewer("1.5.0-a.01", "1.5.0-a.1"));
        // A prerelease number past 64 bits still compares by value.
        Assert.True(ControlVersion.IsNewer("1.5.0-a.18446744073709551616", "1.5.0-a.18446744073709551615"));
    }
}

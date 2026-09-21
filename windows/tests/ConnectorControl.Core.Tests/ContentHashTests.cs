namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/ContentHashTests.swift</summary>
public class ContentHashTests
{
    [Fact]
    public void KnownVector()
    {
        Assert.Equal("sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ContentHash.Sha256("abc"u8.ToArray()));
    }
}

namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/CredentialHeuristicsTests.swift</summary>
public class CredentialHeuristicsTests
{
    [Fact]
    public void KnownPrefixesAndLongMixedTokens()
    {
        Assert.True(CredentialHeuristics.LooksLikeCredential("sk-live-9f3a"));
        Assert.True(CredentialHeuristics.LooksLikeCredential("ghp_abc123"));
        Assert.True(CredentialHeuristics.LooksLikeCredential("xoxb-1-2"));
        Assert.True(CredentialHeuristics.LooksLikeCredential("Bearer abc"));
        Assert.True(CredentialHeuristics.LooksLikeCredential("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6"));
        Assert.True(CredentialHeuristics.LooksLikeCredential("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4"));   // 32 characters is the boundary
        Assert.False(CredentialHeuristics.LooksLikeCredential("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d"));   // 31 is below it
    }

    [Fact]
    public void OrdinaryArgumentsAreNotFlagged()
    {
        Assert.False(CredentialHeuristics.LooksLikeCredential("-y"));
        Assert.False(CredentialHeuristics.LooksLikeCredential("@dbt/mcp"));
        Assert.False(CredentialHeuristics.LooksLikeCredential("https://example.com/mcp"));
        Assert.False(CredentialHeuristics.LooksLikeCredential("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        Assert.False(CredentialHeuristics.LooksLikeCredential("${CC_NEEDS:token}"));
    }
}

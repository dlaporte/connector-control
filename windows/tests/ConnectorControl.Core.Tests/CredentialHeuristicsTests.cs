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
        // A digit is a decimal digit: a superscript or a vulgar fraction is not one.
        Assert.False(CredentialHeuristics.LooksLikeCredential("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\u00B2\u00BD"));
    }

    /// <summary>The same text reads the same on both platforms: this side compares, counts and lowers UTF-16
    /// units, so a combining mark must not hide a name or a prefix on the Mac, and a character beyond the
    /// Basic Plane counts as its two units, neither a letter nor a digit.</summary>
    [Fact]
    public void TextIsReadUnitByUnitAsOnWindows()
    {
        Assert.True(CredentialHeuristics.NamesASecret("--api-key\u0301"));
        Assert.True(CredentialHeuristics.NamesASecret("API_TOKEN\u0301"));
        Assert.True(CredentialHeuristics.NamesASecret("X-API-\u212AEY"));   // the Kelvin sign lowers to k
        Assert.True(CredentialHeuristics.LooksLikeCredential("sk-\u0301live"));
        Assert.False(CredentialHeuristics.LooksLikeCredential("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4 \u0301x"));   // a space is a space
        Assert.True(CredentialHeuristics.LooksLikeCredential("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3\U0001F600"));   // 32 units
        // A letter beyond the Basic Plane is two surrogate units, neither a letter.
        Assert.False(CredentialHeuristics.LooksLikeCredential(new string('1', 31) + "\U0001D538"));
        Assert.True(CredentialHeuristics.LooksLikeRandomToken("AbcdefghijKLMNOPQ1\U0001F600"));   // 20 units
    }
}

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
        // \u0130 lowers to two scalars, i and a combining dot, on the Mac; ToLowerInvariant leaves it as it is.
        Assert.False(CredentialHeuristics.NamesASecret("CREDENT\u0130AL"));
        Assert.Equal("\u0130i", "\u0130I".ToLowerInvariant());
        Assert.True(CredentialHeuristics.LooksLikeCredential("sk-\u0301live"));
        Assert.False(CredentialHeuristics.LooksLikeCredential("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4 \u0301x"));   // a space is a space
        Assert.True(CredentialHeuristics.LooksLikeCredential("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3\U0001F600"));   // 32 units
        // A letter beyond the Basic Plane is two surrogate units, neither a letter.
        Assert.False(CredentialHeuristics.LooksLikeCredential(new string('1', 31) + "\U0001D538"));
        Assert.True(CredentialHeuristics.LooksLikeRandomToken("AbcdefghijKLMNOPQ1\U0001F600"));   // 20 units
    }

    /// <summary>Variables whose value names code to run, whatever their case.</summary>
    [Fact]
    public void NamesAProgramToRun()
    {
        foreach (var name in new[] { "GIT_ASKPASS", "SSH_ASKPASS", "SSH_ASKPASS_REQUIRE", "SUDO_ASKPASS", "MY_TOOL_ASKPASS", "git_askpass",
                     "GIT_SSH", "GIT_SSH_COMMAND", "GIT_EXEC_PATH", "GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0",
                     "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "NODE_OPTIONS", "Node_Options", "LD_PRELOAD", "DYLD_INSERT_LIBRARIES",
                     "PYTHONSTARTUP", "BASH_ENV", "ENV", "env", "PERL5OPT", "RUBYOPT", "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS" })
        {
            Assert.True(CredentialHeuristics.NamesAProgramToRun(name), name);
        }
        foreach (var name in new[] { "GITHUB_TOKEN", "API_KEY", "PASSWORD", "ASKPASS_HINT", "GIT_CONFIG", "ENVIRONMENT", "MY_ENV", "PATH", "NODE_ENV",
                     "LD_PRELOAD_X", "GIT_SSH_X" })
        {
            Assert.False(CredentialHeuristics.NamesAProgramToRun(name), name);
        }
    }

    [Fact]
    public void SchemeWords()
    {
        foreach (var word in new[] { "Bearer", "bearer", "BASIC", "Token" })
        {
            Assert.True(CredentialHeuristics.IsSchemeWord(word), word);
        }
        foreach (var word in new[] { "Bearer ", "Digest", "tokens", "", "Bear" })
        {
            Assert.False(CredentialHeuristics.IsSchemeWord(word), word);
        }
    }

    /// <summary>Beside its references a value may hold only a scheme word, before them: any other word, or
    /// one after a reference, can be the secret itself.</summary>
    [Fact]
    public void AReferenceTakesOnlyASchemeWordBeforeIt()
    {
        foreach (var value in new[] { "${API_TOKEN}", "$API_TOKEN", "Bearer ${API_TOKEN}", "basic $CRED", "Token ${CC_NEEDS:T}", "${A} ${B}", "Bearer${A}" })
        {
            Assert.True(CredentialHeuristics.IsReference(value), value);
        }
        foreach (var value in new[] { "${CC_NEEDS:PW}hunter", "${A} hunter", "hunter ${A}", "${A} Bearer", "Bearer ${A} extra", "Digest ${A}", "Bearer" })
        {
            Assert.False(CredentialHeuristics.IsReference(value), value);
        }
    }
}

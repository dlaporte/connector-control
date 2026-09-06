namespace ConnectorControl.Core.Tests;

public class StringExtensionsTests
{
    [Theory]
    [InlineData("  name  ", "name")]
    [InlineData("\tname\t", "name")]
    [InlineData(" name　", "name")]        // NBSP and ideographic space are Zs
    [InlineData("\nname\n", "\nname\n")]            // newlines are NOT trimmed (Swift .whitespaces)
    [InlineData(" \r\n ", "\r\n")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void TrimSpacesMatchesSwiftWhitespaces(string input, string expected)
    {
        Assert.Equal(expected, input.TrimSpaces());
    }
}

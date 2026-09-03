namespace ConnectorControl.Core.Tests;

public class AppleKeyCollationTests
{
    [Fact]
    public void OrdersLikeAppleJsonSerializationSortedKeys()
    {
        // Verified against JSONSerialization on macOS 26 (see plan Task 6 table).
        // Punctuation-vs-punctuation cases ("aws mcp" / "aws_mcp" / "aws-mcp")
        // are deliberately excluded: Apple's and ICU's variable-weighting may differ.
        string[] expected =
        [
            "", "9", "10", "a1", "a2", "a10", "AWS", "aws2", "aws10",
            "mcpservers", "mcpServers", "MCPServers", "n", "ñ", "o",
            "Service", "service-now", "servicenow", "Zebra", "zeta",
        ];
        var shuffled = expected.Reverse().ToArray();
        var sorted = shuffled.OrderBy(k => k, AppleKeyCollation.Comparer).ToArray();
        Assert.Equal(expected, sorted);
    }

    [Fact]
    public void IsDeterministicForEqualIgnoringCase()
    {
        Assert.True(AppleKeyCollation.Comparer.Compare("abc", "ABC") < 0);
        Assert.True(AppleKeyCollation.Comparer.Compare("ABC", "abc") > 0);
        Assert.Equal(0, AppleKeyCollation.Comparer.Compare("abc", "abc"));
    }
}

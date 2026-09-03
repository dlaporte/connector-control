namespace ConnectorControl.Core.Tests;

public class AppleDoubleFormatterTests
{
    [Theory]
    [InlineData(1e-5, "1e-05")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.0001234, "0.0001234")]
    [InlineData(1.234e-5, "1.234e-05")]
    [InlineData(0.5, "0.5")]
    [InlineData(0.1, "0.1")]
    [InlineData(2.5, "2.5")]
    [InlineData(1234567.891011, "1234567.891011")]
    [InlineData(123456789012345.67, "123456789012345.67")]
    [InlineData(1000000000000000.5, "1000000000000000.5")]
    [InlineData(9007199254740992.0, "9007199254740992")]
    [InlineData(9007199254740994.0, "9.007199254740994e+15")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1.5e300, "1.5e+300")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(2.2250738585072014e-308, "2.2250738585072014e-308")]
    [InlineData(100.0, "100")]
    [InlineData(-3.25, "-3.25")]
    [InlineData(-0.0, "-0")]
    [InlineData(0.0, "0")]
    public void ShortestMatchesSwiftDescription(double value, string expected)
    {
        Assert.Equal(expected, AppleDoubleFormatter.Format(value, JsonNumberStyle.Shortest));
    }

    [Theory]
    [InlineData(1e-5, "1.0000000000000001e-05")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.1, "0.10000000000000001")]
    [InlineData(1.0 / 3.0, "0.33333333333333331")]
    [InlineData(2.5, "2.5")]
    [InlineData(100.25, "100.25")]
    [InlineData(123456789.123, "123456789.123")]
    [InlineData(1e15, "1000000000000000")]
    [InlineData(1e16, "10000000000000000")]
    [InlineData(1e17, "1e+17")]
    [InlineData(5e-324, "4.9406564584124654e-324")]
    [InlineData(2.0, "2")]
    public void G17MatchesAppleJsonSerialization(double value, string expected)
    {
        Assert.Equal(expected, AppleDoubleFormatter.Format(value, JsonNumberStyle.G17));
    }
}

using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.State;

/// <summary>Mirror: Tests/ConnectorControlStateTests/ControlTextTests.swift</summary>
public class ControlTextTests
{
    [Fact]
    public void LabelledLinesLineUpTheirValues()
    {
        Assert.Equal(
            [
                "Active collection    Harvard",
                "Claude integration   on",
                "                     more",
            ],
            ControlText.Labelled([("Active collection", "Harvard"), ("Claude integration", "on"), ("", "more")]));
    }

    [Fact]
    public void ATablePadsEveryColumnButTheLast()
    {
        Assert.Equal(
            [
                "      NAME      RUNS",
                "on    aws-mcp   aws-mcp.us-east-1.api.aws",
                "off   café      npx x",
            ],
            ControlText.Table(["", "NAME", "RUNS"], [["on", "aws-mcp", "aws-mcp.us-east-1.api.aws"], ["off", "café", "npx x"]]));
    }

    /// <summary>A character outside the Basic Multilingual Plane is one scalar on the Mac and two UTF-16 units
    /// here, where widths count runes: both count it once, so both pad alike.</summary>
    [Fact]
    public void ACharacterBeyondTheBasicPlaneIsOneColumnWide()
    {
        Assert.Equal(3, ControlText.Width("a😀b"));
        Assert.Equal(["😀    x", "ab   y"], ControlText.Labelled([("😀", "x"), ("ab", "y")]));
        Assert.Equal(["𝔸𝔹    1", "abc   2"], ControlText.Table(null, [["𝔸𝔹", "1"], ["abc", "2"]]));
    }

    [Fact]
    public void NamesAreListedTheWayTheAppsSentencesListThem()
    {
        Assert.Equal("“a”", ControlText.QuotedList(["a"]));
        Assert.Equal("“a” and “b”", ControlText.QuotedList(["a", "b"]));
        Assert.Equal("“a”, “b” and “c”", ControlText.QuotedList(["a", "b", "c"]));
    }
}

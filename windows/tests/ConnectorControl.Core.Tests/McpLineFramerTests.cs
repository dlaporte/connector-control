using System.Text;

namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/McpLineFramerTests.swift</summary>
public class McpLineFramerTests
{
    private static string[] Shown(IReadOnlyList<McpFrame> frames) =>
        frames.Select(frame => frame is McpFrame.Line line ? Encoding.UTF8.GetString(line.Bytes) : "<too large>").ToArray();

    [Fact]
    public void LinesAreSplitAtNewlinesWhereverTheChunksBreak()
    {
        var framer = new McpLineFramer(10);
        Assert.Empty(framer.Append("ab"u8));
        Assert.Equal(["abc", "de", ""], Shown(framer.Append("c\nde\n\nf"u8)));
        Assert.Equal(["f"], Shown(framer.Finish())); // a last line without its newline is still a line
        Assert.Empty(framer.Finish());
    }

    [Fact]
    public void ALineAtTheLimitPassesAndOnePastItIsReportedOnceAndSkipped()
    {
        var framer = new McpLineFramer(4);
        Assert.Equal(["abcd"], Shown(framer.Append("abcd\n"u8)));
        Assert.Empty(framer.Append("abc"u8));
        Assert.Equal(["<too large>"], Shown(framer.Append("de"u8))); // reported as soon as it passes the limit
        Assert.Empty(framer.Append("fgh"u8)); // the rest of that line is dropped as it comes
        Assert.Equal(["ok"], Shown(framer.Append("ij\nok\n"u8))); // the stream carries on after it
        Assert.Equal(["<too large>"], Shown(framer.Append("toolong\n"u8))); // a whole line past the limit in one chunk
        Assert.Equal(["<too large>"], Shown(framer.Append("waytoolong"u8)));
        Assert.Empty(framer.Finish()); // a skipped line is not a line at the end either
    }

    [Fact]
    public void ACarriageReturnStaysInTheLineAndACharacterMayBeSplitBetweenChunks()
    {
        var framer = new McpLineFramer(10);
        Assert.Equal(["ab\r"], Shown(framer.Append("ab\r\n"u8))); // only the newline is stripped; the session trims the rest
        Assert.Empty(framer.Append([0xC3]));
        Assert.Equal(["é"], Shown(framer.Append([0xA9, 0x0A]))); // bytes, not characters: a split character joins up
    }

    [Fact]
    public void TheDefaultLimitIsOneMegabyte()
    {
        var framer = new McpLineFramer();
        Assert.Equal(McpSession.MaxMessageBytes, framer.Limit);
        var exact = Enumerable.Repeat((byte)0x20, McpSession.MaxMessageBytes).ToArray();
        var frames = framer.Append([.. exact, (byte)'\n']);
        Assert.True(Assert.IsType<McpFrame.Line>(Assert.Single(frames)).Bytes.SequenceEqual(exact));
        Assert.Equal(["<too large>"], Shown(framer.Append([.. exact, (byte)' ', (byte)'\n'])));
    }
}

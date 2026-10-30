using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.State;

/// <summary>Mirror: Tests/ConnectorControlStateTests/ReadmeHelpTests.swift
///
/// README.md's command reference is `connector-control help`'s own output, pasted between two
/// markers. A command added, renamed or re-described without the README following fails here.</summary>
public class ReadmeHelpTests
{
    internal const string Begin = "<!-- connector-control help: begin -->";
    internal const string End = "<!-- connector-control help: end -->";

    /// <summary>Platform idiom: the project file copies README.md beside the test binary, as it copies
    /// the fixtures; the Swift side finds it from its own source path.</summary>
    private static string ReadmePath => Path.Combine(AppContext.BaseDirectory, "README.md");

    /// <summary>The lines inside the one ```text fence between the markers, or null when the README has no
    /// such block or more than one. Line endings are read either way: a Windows checkout may
    /// convert them.</summary>
    internal static string? HelpBlock(string readme)
    {
        var lines = readme.Replace("\r\n", "\n").Split('\n');
        if (lines.Count(l => l == Begin) != 1 || lines.Count(l => l == End) != 1) return null;
        var first = Array.IndexOf(lines, Begin);
        var last = Array.IndexOf(lines, End);
        if (first >= last) return null;
        var inner = lines[(first + 1)..last];
        if (inner.Length < 2 || inner[0] != "```text" || inner[^1] != "```") return null;
        return string.Join("\n", inner[1..^1]);
    }

    [Fact]
    public void TheReadmeListsExactlyWhatHelpPrints()
    {
        var block = HelpBlock(File.ReadAllText(ReadmePath));
        Assert.True(block is not null, $"README.md needs one ```text block between {Begin} and {End}");
        Assert.Equal(ControlTools.HelpText(), block);
    }

    [Fact]
    public void TheBlockIsReadOnlyFromBetweenOneMarkerPair()
    {
        const string b = Begin, e = End;
        Assert.Null(HelpBlock("no markers"));
        Assert.Equal("a\n  b", HelpBlock($"x\n{b}\n```text\na\n  b\n```\n{e}\ny"));
        Assert.Equal("a", HelpBlock($"{b}\r\n```text\r\na\r\n```\r\n{e}\r\n"));
        Assert.Null(HelpBlock($"{b}\na\n{e}"));                                   // no fence
        Assert.Null(HelpBlock($"{e}\n```text\na\n```\n{b}"));                     // markers reversed
        Assert.Null(HelpBlock($"{b}\n```text\na\n```\n{e}\n{b}\n```text\na\n```\n{e}"));   // two blocks
    }
}

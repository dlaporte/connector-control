namespace ConnectorControl.Core.State;

/// <summary>
/// Mirror: Sources/ConnectorControlState/ToolFooterLine.swift
///
/// One line of the footer under Settings ▸ Claude ▸ Tools: how to install a family one of whose tools has a
/// problem. Each row says only what is wrong, so it stays one line tall; what to do is said here, once a family.
/// </summary>
/// <param name="Text">The whole line: the family's tools, the link's title, "or run" and the command.</param>
/// <param name="Runs">The line with the link's title and the command set apart: the view draws the one as a link and
/// the other in the code face.</param>
// Platform idiom: the Mac's line also carries the advice for a tool only the login shell sees; Windows has no such
// state, so no such member.
public sealed record ToolFooterLine(string Text, IReadOnlyList<TextRun> Runs, string LinkTitle, string LinkUrl, string InstallCommand)
{
    public static string InstallText(string tools, string linkTitle, string command) => $"{tools}: {linkTitle} {ToolNote.OrRun} {command}.";

    /// <summary>A line for each family with a tool that has a problem, in the rows' order; none while every tool is
    /// found or still being checked.</summary>
    public static IReadOnlyList<ToolFooterLine> Make(IReadOnlyDictionary<Tool, ToolStatus> statuses)
    {
        var families = new List<ToolFamily>();
        foreach (var tool in ToolInfo.All)
        {
            if (ToolNote.Make(tool, statuses.TryGetValue(tool, out var status) ? status : null) is null)
            {
                continue;
            }
            if (!families.Contains(ToolInfo.Family(tool)))
            {
                families.Add(ToolInfo.Family(tool));
            }
        }
        return families.Select(family =>
        {
            var text = InstallText(ToolInfo.ToolNames(family), ToolInfo.LinkTitle(family), ToolInfo.InstallCommand(family));
            return new ToolFooterLine(text, TextRun.Runs(text, [ToolInfo.LinkTitle(family), ToolInfo.InstallCommand(family)]),
                ToolInfo.LinkTitle(family), ToolInfo.LinkUrl(family), ToolInfo.InstallCommand(family));
        }).ToList();
    }
}

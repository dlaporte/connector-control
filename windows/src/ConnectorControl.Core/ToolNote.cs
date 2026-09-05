namespace ConnectorControl.Core;

/// <summary>
/// The editor note and the Settings row text for one tool (spec §3.4, §3.5, strings §5).
/// <c>Text</c> is line 1; line 2 is <c>LinkTitle</c> (a link to <c>LinkUrl</c>), the words
/// <see cref="OrRun"/>, then <c>InstallCommand</c>. Both platforms carry these strings verbatim.
/// </summary>
public sealed record ToolNote(string Text, string LinkTitle, string LinkUrl, string InstallCommand)
{
    public const string OrRun = "or run";
    public const string CheckingText = "Checking…";
    public const string FoundText = "Found";
    public const string NotFoundText = "Not found";
    public const string SettingsHeader = "Tools";
    public const string SettingsCaption = "Connectors that run through npx, node, uvx or uv need them installed where Claude Desktop can find them.";

    public static string MissingText(Tool tool) =>
        $"{ToolInfo.Name(tool)} wasn’t found, so Claude Desktop won’t be able to start this connector.";

    /// <summary>Null while the status is unknown or the tool is found.</summary>
    public static ToolNote? For(Tool tool, ToolStatus? status)
    {
        if (status is null || status.Found)
        {
            return null;
        }
        var family = ToolInfo.Family(tool);
        return new ToolNote(MissingText(tool), ToolInfo.LinkTitle(family), ToolInfo.LinkUrl(family), ToolInfo.InstallCommand(family));
    }

    /// <summary>The Settings row's right-hand text.</summary>
    public static string StatusText(ToolStatus? status) => status switch
    {
        null => CheckingText,
        { Found: false } => NotFoundText,
        { Version: { } version } => version,
        _ => FoundText,
    };
}

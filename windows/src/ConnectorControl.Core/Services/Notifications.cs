namespace ConnectorControl.Core.Services;

/// <summary>Identifiers shared by the notifier and AppState (Mac: restartCategoryID / restartActionID).</summary>
public static class Notifications
{
    public const string Title = "Connector Control";
    public const string RestartCategory = "restartPending";
    public const string RestartAction = "restartClaude";
    public const string RestartButton = "Restart Claude";
}

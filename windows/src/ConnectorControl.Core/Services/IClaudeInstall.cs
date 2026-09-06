namespace ConnectorControl.Core.Services;

public interface IClaudeInstall
{
    /// <summary>Detects the current install each time it is called (the user may install or update Claude while we run).</summary>
    ClaudeInstallInfo Detect();
}

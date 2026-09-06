namespace ConnectorControl.Core.State;

public enum UpdateOutcome
{
    /// <summary>Not an installed build; the updater is inert.</summary>
    Unavailable,
    UpToDate,
    Failed,
    /// <summary>Downloaded silently; applies when the app quits.</summary>
    StagedForQuit,
    /// <summary>The user chose Later.</summary>
    Deferred,
    /// <summary>The user chose Install and Relaunch; the process is about to restart.</summary>
    Installing,
}

namespace ConnectorControl.Core.State;

/// <summary>
/// What one connector of an imported document does about the connector the target collection
/// already has under that name. <see cref="Add"/> is the case where nothing is in the way. Each
/// caller picks its own default for a collision: Import leaves the row unticked, which sends
/// <see cref="Skip"/>, and preselects Replace should it be ticked; Copy preselects Keep both,
/// AppState's own default.
///
/// Mirror: Sources/ConnectorControlState/ImportChoice.swift
/// </summary>
public enum ImportChoice
{
    Add,
    Replace,
    KeepBoth,
    Skip,
}

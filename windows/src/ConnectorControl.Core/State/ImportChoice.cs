namespace ConnectorControl.Core.State;

/// <summary>
/// What one connector of an imported document does about the connector the target collection
/// already has under that name. <see cref="Add"/> is the case where nothing is in the way;
/// <see cref="Skip"/> is the default for a collision, so an import never overwrites anything the
/// user did not ask it to.
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

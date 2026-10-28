namespace ConnectorControl.Core.State;

/// <summary>Mirror: Sources/ConnectorControlState/Control/ControlTools+Writes.swift</summary>
public sealed partial class ControlTools
{
    /// <summary>Prepares the write <paramref name="name"/> against the state as it is now: every refusal is
    /// found here, before anyone is asked, and the change is performed only by the step's <c>Perform</c>.</summary>
    internal ControlStep Prepare(string name, ControlArguments arguments, ControlCaller caller) => name switch
    {
        _ => new ControlStep.Refused(UnknownToolError(name)),
    };
}

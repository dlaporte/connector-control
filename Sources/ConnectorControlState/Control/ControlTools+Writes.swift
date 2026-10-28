import ConnectorControlCore

/// Mirror: windows/src/ConnectorControl.Core/State/Control/ControlTools.Writes.cs
extension ControlTools {
    /// Prepares the write `name` against the state as it is now: every refusal is found here, before
    /// anyone is asked, and the change is performed only by the step's `perform`.
    func prepare(_ name: String, _ arguments: ControlArguments, _ caller: ControlCaller) -> ControlStep {
        if let prepareOverride { return prepareOverride(name, arguments, caller) }
        switch name {
        default:
            return .refused(ControlTools.unknownToolError(name))
        }
    }
}

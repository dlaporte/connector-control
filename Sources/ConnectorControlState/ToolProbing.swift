import ConnectorControlCore

/// The probe as a seam so AppState can be driven by a fake machine.
public protocol ToolProbing: Sendable {
    func probe(_ tools: [Tool]) -> [Tool: ToolStatus]
}

extension ToolProbe: ToolProbing {}

import ConnectorControlCore

public extension MasterStore {
    /// Convenience used across tests: a single-profile store.
    static func single(_ mcps: [String: MCPEntry]) -> MasterStore {
        MasterStore(activeProfile: "Default", profiles: ["Default": Profile(mcps: mcps)])
    }
}

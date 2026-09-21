import ConnectorControlCore

public extension MasterStore {
    /// Convenience used across tests: a single-collection store.
    static func single(_ mcps: [String: MCPEntry]) -> MasterStore {
        MasterStore(activeCollection: "Default", collections: ["Default": Collection(mcps: mcps)])
    }
}

import ConnectorControlCore

/// The string `args` of a `{"args": [...]}`-shaped config, or nil when the
/// config isn't shaped that way. Shared by RemoteAuthTests and PasteRecoveryTests.
public func args(of config: JSONValue) -> [String]? {
    guard case .object(let o) = config, case .array(let a)? = o["args"] else { return nil }
    return a.compactMap { if case .string(let s) = $0 { return s }; return nil }
}

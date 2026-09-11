import Foundation
import ConnectorControlCore
@testable import ConnectorControlState

extension EditTarget {
    /// Test-only factory for a brand-new connector opened from a template
    /// config (e.g. the Local-form default). Production code only ever opens
    /// a genuinely blank target (`.newRemote()`) or an existing one.
    static func new(template: JSONValue) -> EditTarget {
        EditTarget(id: UUID().uuidString, name: "", entry: MCPEntry(config: template), isNew: true)
    }
}

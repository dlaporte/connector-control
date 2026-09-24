import Foundation
import ConnectorControlCore
@testable import ConnectorControlState

extension EditTarget {
    /// The collection a target names when a test does not say: the one every harness starts
    /// with, and active. Production always names the collection the window shows.
    static let testCollection = "Default"

    /// Test-only factory for a brand-new connector opened from a template
    /// config (e.g. the Local-form default). Production code only ever opens
    /// a genuinely blank target (`.newRemote(in:)`) or an existing one.
    static func new(template: JSONValue, in collection: String = testCollection) -> EditTarget {
        EditTarget(id: UUID().uuidString, name: "", entry: MCPEntry(config: template), isNew: true,
                   collection: collection)
    }

    static func existing(name: String, entry: MCPEntry) -> EditTarget {
        existing(name: name, entry: entry, in: testCollection)
    }

    static func newRemote() -> EditTarget { newRemote(in: testCollection) }
}

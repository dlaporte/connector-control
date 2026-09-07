import Foundation
@testable import ConnectorControlState

struct FakeAutostartError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

final class FakeAutostart: Autostart {
    var enabled = false
    var requiresApproval = false
    /// When set, setEnabled throws an error whose localizedDescription is this text.
    var failWith: String?
    private(set) var setCalls = 0

    var isEnabled: Bool { enabled }

    func setEnabled(_ on: Bool) throws {
        setCalls += 1
        if let failWith { throw FakeAutostartError(message: failWith) }
        enabled = on
    }
}

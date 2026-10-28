@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/TestSupport/FakeControlConfirmer.cs
///
/// The confirmation dialog as a seam: it records every question, and answers each at once with
/// `scripted`, or holds it until the test calls `answer(_:)`. The Windows mirror can also throw, to
/// show a dialog that fails cannot wedge the write queue; a Swift confirmer cannot throw.
@MainActor
final class FakeControlConfirmer: ControlConfirmer {
    /// Answers every question at once when set; nil holds them for `answer(_:)`.
    var scripted: Bool?
    private(set) var requests: [ConfirmationRequest] = []
    private var waiting: [(Bool) -> Void] = []

    var waitingCount: Int { waiting.count }

    func confirm(_ request: ConfirmationRequest, completion: @escaping (Bool) -> Void) {
        requests.append(request)
        if let scripted { completion(scripted) } else { waiting.append(completion) }
    }

    /// The user answers the oldest question still open.
    func answer(_ allow: Bool) {
        waiting.removeFirst()(allow)
    }
}

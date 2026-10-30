@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/TestSupport/FakeControlConfirmer.cs
///
/// The confirmation dialog as a seam: it records every question, and answers each at once with
/// `scripted`, or holds it until the test calls `answer(_:)`. `whileOpen` stands in for a modal
/// dialog's own run loop, which carries on with what the host delays before `confirm` returns. The
/// Windows mirror can also throw, to show a dialog that fails cannot wedge the write queue; a Swift
/// confirmer cannot throw.
@MainActor
final class FakeControlConfirmer: ControlConfirmer {
    /// Answers every question at once when set; nil holds them for `answer(_:)`.
    var scripted: Bool?
    /// Runs once each question is recorded and before it is answered, as a modal dialog's run loop
    /// runs what is due while the user reads it.
    var whileOpen: (() -> Void)?
    private(set) var requests: [ConfirmationRequest] = []
    private var waiting: [(Bool) -> Void] = []

    var waitingCount: Int { waiting.count }

    func confirm(_ request: ConfirmationRequest, completion: @escaping (Bool) -> Void) {
        requests.append(request)
        whileOpen?()
        if let scripted { completion(scripted) } else { waiting.append(completion) }
    }

    /// The user answers the oldest question still open.
    func answer(_ allow: Bool) {
        waiting.removeFirst()(allow)
    }
}

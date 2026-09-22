@testable import ConnectorControlState

final class FakeDialogs: Dialogs {
    struct ConfirmCall: Equatable {
        let message: String
        let informative: String?
        let primary: String
        let cancel: String
        let destructive: Bool
    }

    struct PromptCall: Equatable {
        let title: String
        let initial: String
    }

    var nextConfirm = true
    /// Answers for a flow that raises more than one confirmation, taken in order; `nextConfirm`
    /// answers whatever is left. One flag cannot say "delete it, but keep the file".
    var confirmAnswers: [Bool] = []
    var nextPromptAnswer: String?
    private(set) var confirms: [ConfirmCall] = []
    private(set) var prompts: [PromptCall] = []

    func confirm(message: String, informative: String?, primary: String, cancel: String, destructive: Bool) -> Bool {
        confirms.append(ConfirmCall(message: message, informative: informative, primary: primary,
                                    cancel: cancel, destructive: destructive))
        return confirmAnswers.isEmpty ? nextConfirm : confirmAnswers.removeFirst()
    }

    func promptForName(title: String, initial: String) -> String? {
        prompts.append(PromptCall(title: title, initial: initial))
        return nextPromptAnswer
    }
}

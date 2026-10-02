@testable import ConnectorControlState

final class FakeDialogs: Dialogs {
    struct ConfirmCall: Equatable {
        let message: String
        let informative: String?
        let primary: String
        let cancel: String
        let destructive: Bool
        var cancelIsDefault = false
    }

    struct PromptCall: Equatable {
        let title: String
        let initial: String
    }

    struct InformCall: Equatable {
        let message: String
        let informative: String?
    }

    var nextConfirm = true
    /// Answers for a flow that raises more than one confirmation, taken in order; `nextConfirm`
    /// answers whatever is left. One flag cannot say "delete it, but keep the file".
    var confirmAnswers: [Bool] = []
    var nextPromptAnswer: String?
    private(set) var confirms: [ConfirmCall] = []
    private(set) var prompts: [PromptCall] = []
    private(set) var informs: [InformCall] = []

    func confirm(message: String, informative: String?, primary: String, cancel: String, destructive: Bool,
                 cancelIsDefault: Bool) -> Bool {
        confirms.append(ConfirmCall(message: message, informative: informative, primary: primary,
                                    cancel: cancel, destructive: destructive, cancelIsDefault: cancelIsDefault))
        return confirmAnswers.isEmpty ? nextConfirm : confirmAnswers.removeFirst()
    }

    func promptForName(title: String, initial: String) -> String? {
        prompts.append(PromptCall(title: title, initial: initial))
        return nextPromptAnswer
    }

    func inform(message: String, informative: String?) {
        informs.append(InformCall(message: message, informative: informative))
    }
}

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
    var nextPromptAnswer: String?
    private(set) var confirms: [ConfirmCall] = []
    private(set) var prompts: [PromptCall] = []

    func confirm(message: String, informative: String?, primary: String, cancel: String, destructive: Bool) -> Bool {
        confirms.append(ConfirmCall(message: message, informative: informative, primary: primary,
                                    cancel: cancel, destructive: destructive))
        return nextConfirm
    }

    func promptForName(title: String, initial: String) -> String? {
        prompts.append(PromptCall(title: title, initial: initial))
        return nextPromptAnswer
    }
}

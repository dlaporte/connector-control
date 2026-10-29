import Foundation

/// Mirror: windows/src/ConnectorControl.Core/State/Control/ConfirmationDialog.cs
///
/// A confirmation as the app draws it, from the question the tools put: the title and message with
/// every name set apart and none longer than `nameLimit`, the lines of what would run, each with its
/// kind, and the closing line. The views draw it with Don’t Allow the default — Return and Escape
/// answer it, and so does closing the window — and every paragraph left to right, whatever a name
/// begins with, so a name that starts right to left cannot turn the sentence around it. What is
/// compared on Allow is the request, never this: the cut is drawing only.
public struct ConfirmationDialog: Equatable, Sendable {
    public static let allowButton = "Allow"
    public static let dontAllowButton = "Don’t Allow"
    /// The longest a name runs, in scalars, before it is cut: a name the length of a paragraph could
    /// push the rest of the question out of sight.
    public static let nameLimit = 64

    /// What a line of a change does: the views colour it, and its own "−" or "+" still says so.
    public enum LineKind: Equatable, Sendable { case plain, removed, added }

    public let title: [TextRun]
    public let message: [TextRun]
    public let code: [String]
    public let codeKinds: [LineKind]
    /// The closing line; a path in it is set apart whole.
    public let footer: [TextRun]?

    @MainActor
    public init(_ request: ConfirmationRequest) {
        title = TextRun.names(in: request.title, limit: ConfirmationDialog.nameLimit)
        message = TextRun.names(in: request.message, limit: ConfirmationDialog.nameLimit)
        code = request.code
        // A closure, not the function itself: passing a main-actor function as a value to map would
        // lose its isolation.
        codeKinds = request.code.map { ConfirmationDialog.kind($0) }
        footer = request.footer.map { TextRun.names(in: $0, limit: .max) }
    }

    /// A line a change removes or adds, by the prefix the change lines give it; any other is plain.
    @MainActor
    static func kind(_ line: String) -> LineKind {
        if line.unicodeScalars.starts(with: ControlMasking.removedPrefix.unicodeScalars) { return .removed }
        if line.unicodeScalars.starts(with: ControlMasking.addedPrefix.unicodeScalars) { return .added }
        return .plain
    }
}

/// Mirror: windows/src/ConnectorControl.Core/State/Control/ConfirmationDialog.cs
///
/// The app's confirmer: each question goes to the user through `Dialogs.confirmChange(_:)`, on a
/// turn of its own that `present` gives it, never inside `confirm`. So `confirm` returns at once, and
/// the call's progress and early answer go out while the dialog is open (see `ControlConfirmer`).
/// `completion` is called once, with true only for Allow; the dialog's own Don’t Allow, Escape and
/// close all answer false.
@MainActor
public final class DialogConfirmer: ControlConfirmer {
    private let dialogs: Dialogs
    private let present: (@escaping MainActorAction) -> Void

    public init(dialogs: Dialogs, present: @escaping (@escaping MainActorAction) -> Void) {
        self.dialogs = dialogs
        self.present = present
    }

    public func confirm(_ request: ConfirmationRequest, completion: @escaping (Bool) -> Void) {
        let dialog = ConfirmationDialog(request)
        let dialogs = self.dialogs
        // The Windows mirror catches around the dialog, which can throw once it may be showing; a Swift
        // dialog cannot throw, so its one answer is all there is.
        present { completion(dialogs.confirmChange(dialog)) }
    }
}

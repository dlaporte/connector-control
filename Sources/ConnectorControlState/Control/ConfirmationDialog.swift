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

    // Platform idiom: text is compared scalar by scalar, as the Windows mirror compares it ordinally, and
    // the lists element by element, which a C# class must say itself; Swift's == would hold canonically
    // equivalent text equal, and a name composed another way is another name.
    public static func == (one: ConfirmationDialog, other: ConfirmationDialog) -> Bool {
        one.title == other.title && one.message == other.message
            && one.code.count == other.code.count && zip(one.code, other.code).allSatisfy { TextRun.same($0, $1) }
            && one.codeKinds == other.codeKinds && one.footer == other.footer
    }

    /// A line a change removes or adds, by the prefix the change lines give it — at the line's start,
    /// or after the one indent an update's lines take under their heading; any other is plain. Only a
    /// change line starts so: a line that continues a field is padded past the widest label, deeper
    /// than either. A key that itself reads as a change line is coloured as one, which says nothing its
    /// own "−" or "+" does not.
    @MainActor
    static func kind(_ line: String) -> LineKind {
        if starts(line, ControlMasking.removedPrefix) { return .removed }
        if starts(line, ControlMasking.addedPrefix) { return .added }
        return .plain
    }

    /// Whether `line` starts with `prefix`, or with one indent and then `prefix`, scalar by scalar.
    @MainActor
    static func starts(_ line: String, _ prefix: String) -> Bool {
        line.unicodeScalars.starts(with: prefix.unicodeScalars)
            || line.unicodeScalars.starts(with: (ControlTools.indent + prefix).unicodeScalars)
    }
}

/// Mirror: windows/src/ConnectorControl.Core/State/Control/ConfirmationDialog.cs
///
/// The app's confirmer: each question goes to the user through `Dialogs.confirmChange(_:)`, on a
/// turn of its own that `present` gives it, never inside `confirm`. So `confirm` returns at once, and
/// the call's progress and early answer go out while the dialog is open (see `ControlConfirmer`).
/// `completion` is called once, with true only for Allow; the dialog's own Don’t Allow, Escape and
/// close all answer false.
///
/// Platform idiom: the initialiser defaults its presenter to a turn of the main run loop
/// (`nextRunLoopTurn`); the Windows mirror's assembly cannot name the WPF dispatcher, so its app always
/// passes `UiThread.Post`.
@MainActor
public final class DialogConfirmer: ControlConfirmer {
    private let dialogs: Dialogs
    private let present: (@escaping MainActorAction) -> Void

    public init(dialogs: Dialogs, present: @escaping (@escaping MainActorAction) -> Void = DialogConfirmer.nextRunLoopTurn) {
        self.dialogs = dialogs
        self.present = present
    }

    /// AppKit's NSModalPanelRunLoopMode, named here because this module is Foundation only.
    public nonisolated static let modalPanelMode = RunLoop.Mode("NSModalPanelRunLoopMode")

    // Platform idiom: on the Mac a nested run loop entered from inside a block on the main queue, or a
    // main-actor Task, runs no other main-queue block, and every line from the socket arrives in one; a
    // modal entered from a run-loop turn of its own runs them all. WPF's nested frame runs the
    // dispatcher's queue whatever called it, so the Windows mirror's presenter only posts.
    /// The Mac's presenter, for this confirmer and for any other modal the app enters: `work` on the next
    /// turn of the main run loop, in the default mode or while another of the app's alerts is up —
    /// never inside the block that asked, and never during a menu or a drag. Nonisolated: it only posts.
    public nonisolated static func nextRunLoopTurn(_ work: @escaping MainActorAction) {
        RunLoop.main.perform(inModes: [.default, modalPanelMode]) { MainActor.assumeIsolated(work) }
    }

    public func confirm(_ request: ConfirmationRequest, completion: @escaping (Bool) -> Void) {
        let dialog = ConfirmationDialog(request)
        let dialogs = self.dialogs
        // Platform idiom: the Windows mirror catches around the dialog, which can throw once it may be
        // showing; a Swift dialog cannot throw, so its one answer is all there is.
        present { completion(dialogs.confirmChange(dialog)) }
    }
}

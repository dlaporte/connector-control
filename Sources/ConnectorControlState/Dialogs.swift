/// The NSAlert surfaces as a seam the app implements with `NSAlert` and tests
/// script with a fake. The sheet-style
/// confirmations (loss warning, remove, restore) are not here: their models
/// publish a pending state the SwiftUI `confirmationDialog` binds to.
@MainActor
public protocol Dialogs: AnyObject {
    /// Two-button alert; true when the primary button was chosen. `cancelIsDefault` makes the
    /// cancel button the one Return presses, for a question whose primary answer is not to be
    /// given by reflex; Escape answers the cancel button either way.
    func confirm(message: String, informative: String?, primary: String, cancel: String, destructive: Bool,
                 cancelIsDefault: Bool) -> Bool
    /// Text prompt with OK / Cancel; the raw (untrimmed) text, or nil on Cancel.
    func promptForName(title: String, initial: String) -> String?
    /// One-button alert: something the user is told and can only acknowledge.
    func inform(message: String, informative: String?)
}

public extension Dialogs {
    static var cancelTitle: String { "Cancel" }

    /// The usual default: Return presses the primary button. One label short of the requirement,
    /// so a conformer that forgot it cannot compile against this instead.
    func confirm(message: String, informative: String?, primary: String, cancel: String, destructive: Bool) -> Bool {
        confirm(message: message, informative: informative, primary: primary, cancel: cancel, destructive: destructive,
                cancelIsDefault: false)
    }

    /// The usual shape: a Cancel button. Deliberately NOT the requirement's
    /// label set — an overload with the same labels would double as the
    /// requirement's default implementation, and a conformer that forgot the
    /// method would compile clean and recurse forever at runtime.
    func confirm(message: String, informative: String?, primary: String, destructive: Bool = false) -> Bool {
        confirm(message: message, informative: informative, primary: primary, cancel: Self.cancelTitle, destructive: destructive)
    }
}

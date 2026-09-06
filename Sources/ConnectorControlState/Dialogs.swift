/// The NSAlert surfaces (catalog §1.16, §1.17, §1.18, §3.9) as a seam the app
/// implements with `NSAlert` and tests script with a fake. The sheet-style
/// confirmations (loss warning, remove, restore) are not here: their models
/// publish a pending state the SwiftUI `confirmationDialog` binds to.
public protocol Dialogs: AnyObject {
    /// Two-button alert; true when the primary button was chosen.
    func confirm(message: String, informative: String?, primary: String, cancel: String, destructive: Bool) -> Bool
    /// Text prompt with OK / Cancel; the raw (untrimmed) text, or nil on Cancel.
    func promptForName(title: String, initial: String) -> String?
}

public extension Dialogs {
    static var cancelTitle: String { "Cancel" }

    func confirm(message: String, informative: String?, primary: String,
                 cancel: String = "Cancel", destructive: Bool = false) -> Bool {
        confirm(message: message, informative: informative, primary: primary, cancel: cancel, destructive: destructive)
    }
}

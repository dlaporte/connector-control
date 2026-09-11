import AppKit
import ConnectorControlState

/// App-modal NSAlerts, activating the app first so the alert is not hidden
/// behind whatever is frontmost.
@MainActor
final class AlertDialogs: Dialogs {
    static let okTitle = "OK"

    func confirm(message: String, informative: String?, primary: String, cancel: String, destructive: Bool) -> Bool {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = message
        if let informative { alert.informativeText = informative }
        alert.addButton(withTitle: primary)
        alert.addButton(withTitle: cancel)
        if destructive { alert.buttons.first?.hasDestructiveAction = true }
        return alert.runModal() == .alertFirstButtonReturn
    }

    func promptForName(title: String, initial: String) -> String? {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = title
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 220, height: 24))
        field.stringValue = initial
        alert.accessoryView = field
        alert.window.initialFirstResponder = field
        alert.addButton(withTitle: AlertDialogs.okTitle)
        alert.addButton(withTitle: Self.cancelTitle)
        guard alert.runModal() == .alertFirstButtonReturn else { return nil }
        return field.stringValue
    }
}

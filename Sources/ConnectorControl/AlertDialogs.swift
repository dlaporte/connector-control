import AppKit
import ConnectorControlState

/// App-modal NSAlerts, activating the app first so the alert is not hidden
/// behind whatever is frontmost.
@MainActor
final class AlertDialogs: Dialogs {
    static let okTitle = "OK"

    func confirm(message: String, informative: String?, primary: String, cancel: String, destructive: Bool,
                 cancelIsDefault: Bool) -> Bool {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = message
        if let informative { alert.informativeText = informative }
        let primaryButton = alert.addButton(withTitle: primary)
        // NSAlert gives Escape only to a button titled "Cancel". Keep, the published-file
        // question's second answer, needs it too: Escape answers the cancel button whatever it
        // says, as it does on the Windows dialog's IsCancel button.
        let cancelButton = alert.addButton(withTitle: cancel)
        cancelButton.keyEquivalent = "\u{1b}"
        if destructive { primaryButton.hasDestructiveAction = true }
        guard cancelIsDefault else { return alert.runModal() == .alertFirstButtonReturn }
        // Return presses the cancel button, and the primary answers only a click. A button holds
        // one key equivalent, so Escape reaches the cancel button through a monitor that lives
        // exactly as long as the alert, as the Windows dialog's IsDefault and IsCancel both sit
        // on its cancel button.
        primaryButton.keyEquivalent = ""
        cancelButton.keyEquivalent = "\r"
        let escape = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            guard event.keyCode == 53, event.window == alert.window else { return event }
            cancelButton.performClick(nil)
            return nil
        }
        defer { escape.map(NSEvent.removeMonitor) }
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

    func inform(message: String, informative: String?) {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = message
        if let informative { alert.informativeText = informative }
        alert.addButton(withTitle: AlertDialogs.okTitle)
        alert.runModal()
    }
}

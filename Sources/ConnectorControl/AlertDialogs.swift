import AppKit
import Carbon.HIToolbox
import ConnectorControlState

/// App-modal NSAlerts, activating the app first so the alert is not hidden
/// behind whatever is frontmost.
@MainActor
final class AlertDialogs: Dialogs {
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
        return runSafeDefault(alert, primary: primaryButton, cancel: cancelButton)
    }

    func promptForName(title: String, initial: String) -> String? {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = title
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 220, height: 24))
        field.stringValue = initial
        alert.accessoryView = field
        alert.window.initialFirstResponder = field
        alert.addButton(withTitle: Self.okTitle)
        alert.addButton(withTitle: Self.cancelTitle)
        guard alert.runModal() == .alertFirstButtonReturn else { return nil }
        return field.stringValue
    }

    func inform(message: String, informative: String?) {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = message
        if let informative { alert.informativeText = informative }
        alert.addButton(withTitle: Self.okTitle)
        alert.runModal()
    }

    /// A change Claude asks for: the title as the alert's message, the rest in the accessory view,
    /// and Allow the answer only a click gives. Allow is added first, so Don’t Allow sits left of it.
    func confirmChange(_ dialog: ConfirmationDialog) -> Bool {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = ConfirmationAccessory.title(dialog)
        alert.accessoryView = ConfirmationAccessory.view(dialog)
        let allow = alert.addButton(withTitle: ConfirmationDialog.allowButton)
        let dontAllow = alert.addButton(withTitle: ConfirmationDialog.dontAllowButton)
        // Claude is frontmost when it asks, and activation is a request the frontmost app need not grant,
        // so the alert sits at the modal-panel level, above every app's ordinary windows, whether or not
        // this app comes forward. NSAlert's own modal session uses this level today; the question does not
        // rest on that.
        alert.window.level = .modalPanel
        return runSafeDefault(alert, primary: allow, cancel: dontAllow)
    }

    /// The keys that answer a safe-default alert's cancel button wherever its focus is: Escape, Return
    /// and the keypad's Enter.
    private static let cancelKeys: Set<Int> = [kVK_Escape, kVK_Return, kVK_ANSI_KeypadEnter]

    /// Return presses the cancel button, and the primary answers only a click. Focus starts on the cancel
    /// button: NSAlert starts it on the first, where with keyboard navigation on Space would answer the
    /// primary unclicked. A button holds one key equivalent, and a focused view - a line of the code block
    /// takes focus, from Tab or a click, when the buttons cannot - swallows Return before any button sees it, so Escape,
    /// Return and Enter reach the cancel button through a monitor that lives exactly as long as the
    /// alert, as the Windows dialog's IsDefault and IsCancel both sit on its cancel button.
    private func runSafeDefault(_ alert: NSAlert, primary: NSButton, cancel: NSButton) -> Bool {
        primary.keyEquivalent = ""
        cancel.keyEquivalent = "\r"
        alert.window.initialFirstResponder = cancel
        let keys = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            guard AlertDialogs.cancelKeys.contains(Int(event.keyCode)), event.window == alert.window else { return event }
            cancel.performClick(nil)
            return nil
        }
        defer { keys.map(NSEvent.removeMonitor) }
        return alert.runModal() == .alertFirstButtonReturn
    }
}

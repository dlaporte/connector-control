import SwiftUI
import AppKit

/// Hands the hosting NSWindow to SwiftUI state so it can be closed directly —
/// SwiftUI dismissal actions don't fire reliably from dialog contexts here.
struct WindowFinder: NSViewRepresentable {
    var onFound: (NSWindow) -> Void

    func makeNSView(context: Context) -> NSView {
        let view = ReportingView()
        view.onFound = onFound
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {}

    /// Reports through the proper AppKit hook — it fires exactly when the
    /// view's window changes, so no polling on a dispatch queue is needed.
    private final class ReportingView: NSView {
        var onFound: ((NSWindow) -> Void)?

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            if let window { onFound?(window) }
        }
    }
}

import SwiftUI
import AppKit

/// MenuBarExtra's window grows with its content but doesn't reliably SHRINK
/// when content gets shorter (footer clearing, banner dismissing, row removal),
/// leaving dead space above and below the vertically-centered content. This
/// shim snaps the window frame to the content's fitted height, hung from a
/// recorded top-edge anchor.
///
/// The sizing must be IDEMPOTENT: three resize passes race here (SwiftUI's
/// auto-grow, this shim, the measured scroll-cap feedback), and a violent
/// content change like a profile switch interleaves them. A delta-based
/// correction that preserves the current top edge perpetuates whatever
/// transient frame it happened to read — the frame is therefore always set
/// to absolutes (anchored top, content-ideal height) computed from stable
/// facts, so competing passes converge instead of compounding drift.
struct WindowAutoSizer: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView { TrackingView() }

    func updateNSView(_ nsView: NSView, context: Context) {
        (nsView as? TrackingView)?.scheduleResize()
    }

    final class TrackingView: NSView {
        private var anchoredWindow: NSWindow?
        private var anchorTop: CGFloat?
        /// Tracks visibility TRANSITIONS: the anchor is recorded on the first
        /// observation after the window becomes visible — by whichever runs
        /// first, the occlusion notification or a scheduled resize — because
        /// that is when the system has just positioned the panel under the
        /// status item (a reopen may be on another display). Anchoring at
        /// view-attach time recorded a not-yet-positioned frame, and
        /// re-anchoring on every occlusion event could persist a frame this
        /// shim had itself already moved.
        private var wasVisible = false
        private var resizePending = false
        private var visibilityObserver: NSObjectProtocol?

        deinit {
            if let visibilityObserver {
                NotificationCenter.default.removeObserver(visibilityObserver)
            }
        }

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            observeWindowIfNeeded()
        }

        override func layout() {
            super.layout()
            scheduleResize()
        }

        private func observeWindowIfNeeded() {
            guard let window, window !== anchoredWindow else { return }
            anchoredWindow = window
            anchorTop = nil
            wasVisible = false
            if let visibilityObserver {
                NotificationCenter.default.removeObserver(visibilityObserver)
            }
            visibilityObserver = NotificationCenter.default.addObserver(
                forName: NSWindow.didChangeOcclusionStateNotification,
                object: window, queue: .main
            ) { [weak self, weak window] _ in
                guard let self, let window else { return }
                if window.isVisible {
                    self.reanchorIfShowTransition()
                    self.scheduleResize()
                } else {
                    self.wasVisible = false
                }
            }
        }

        private func reanchorIfShowTransition() {
            guard let window, window.isVisible, !wasVisible else { return }
            anchorTop = window.frame.maxY
            wasVisible = true
        }

        /// Coalesces to one setFrame per runloop turn: setFrame is re-entrant
        /// with layout(), and a profile switch produces several layout passes;
        /// applying once after SwiftUI has settled avoids the frame fights.
        func scheduleResize() {
            guard !resizePending else { return }
            resizePending = true
            DispatchQueue.main.async { [weak self] in
                self?.resizePending = false
                self?.resizeWindowToFit()
            }
        }

        private func resizeWindowToFit() {
            observeWindowIfNeeded()
            reanchorIfShowTransition()
            // Pre-show frames belong to the system's placement pass.
            guard let window, let anchorTop, window.isVisible else { return }
            // This view is the root VStack's background, so its own laid-out
            // height IS the content's ideal height — even while the window is
            // stuck at another size. (contentView.fittingSize just echoes the
            // current frame for hosting views, which is why it can't detect
            // slack.)
            let ideal = bounds.height
            guard ideal > 1 else { return }
            var frame = window.frame
            frame.origin.y = anchorTop - ideal
            frame.size.height = ideal
            guard abs(frame.maxY - window.frame.maxY) > 1
                || abs(frame.height - window.frame.height) > 1 else { return }
            window.setFrame(frame, display: true, animate: false)
        }
    }
}

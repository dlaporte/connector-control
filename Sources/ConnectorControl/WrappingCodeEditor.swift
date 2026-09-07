import SwiftUI
import AppKit

/// A monospaced, word-wrapping plain-text editor. SwiftUI's TextEditor won't
/// char-wrap long unbroken tokens (URLs, secrets); this NSTextView does.
struct WrappingCodeEditor: NSViewRepresentable {
    @Binding var text: String

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSTextView.scrollableTextView()
        scroll.hasHorizontalScroller = false
        scroll.borderType = .noBorder
        guard let tv = scroll.documentView as? NSTextView else { return scroll }
        tv.isRichText = false
        tv.isAutomaticQuoteSubstitutionEnabled = false
        tv.isAutomaticDashSubstitutionEnabled = false
        tv.isAutomaticTextReplacementEnabled = false
        tv.font = .monospacedSystemFont(ofSize: NSFont.smallSystemFontSize, weight: .regular)
        tv.textContainerInset = NSSize(width: 4, height: 6)
        tv.isVerticallyResizable = true
        tv.isHorizontallyResizable = false
        tv.textContainer?.widthTracksTextView = true
        tv.textContainer?.lineBreakMode = .byCharWrapping
        tv.autoresizingMask = [.width]
        tv.delegate = context.coordinator
        tv.string = text
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let tv = scroll.documentView as? NSTextView, tv.string != text else { return }
        tv.string = text
    }

    final class Coordinator: NSObject, NSTextViewDelegate {
        let parent: WrappingCodeEditor
        init(_ p: WrappingCodeEditor) { parent = p }
        func textDidChange(_ n: Notification) {
            guard let tv = n.object as? NSTextView else { return }
            parent.text = tv.string
        }
    }
}

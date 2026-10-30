import SwiftUI

/// Why a sheet's verb did not land, or why its document cannot be read: the model's sentence in
/// full, in red, under whatever it is about. Every sheet, the editor's validation error and the
/// command-line tool's row in Settings draw it this one way; the JSON pane's parse error stays a
/// caption beside its tip. The Windows dialogs share theirs through DialogWindow.
struct FailureLine: View {
    private let text: String

    init(_ text: String) {
        self.text = text
    }

    var body: some View {
        Text(text)
            .font(.callout)
            .foregroundStyle(.red)
            .fixedSize(horizontal: false, vertical: true)
    }
}

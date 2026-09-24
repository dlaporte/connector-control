import SwiftUI

/// Why a sheet's verb did not land, or why its document cannot be read: the model's sentence in
/// full, in red, under whatever it is about. Every sheet and the editor draw it this one way; the
/// Windows dialogs share theirs through DialogWindow.
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

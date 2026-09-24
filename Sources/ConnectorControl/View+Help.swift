import SwiftUI

extension View {
    /// A tooltip that may not exist. An empty tooltip is worse than none, so without a sentence
    /// the view goes up bare.
    @ViewBuilder func help(ifAny text: String?) -> some View {
        if let text { help(text) } else { self }
    }
}

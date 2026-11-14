import AppKit
import SwiftUI

extension View {
    /// A sheet in its window's colour. On macOS 26 and later a sheet is drawn as a floating card with a white
    /// background of its own unless it is given one, while windows paint `windowBackgroundColor`. This paints that
    /// colour behind the whole sheet, edge to edge (behind its padding, ignoring safe areas), and gives the card the
    /// same. Applied once where each sheet is presented; no sheet's own layout changes.
    // Platform idiom: Mac only. The Windows dialogs are windows, and already paint the window background.
    func sheetBackground() -> some View {
        let window = Color(nsColor: .windowBackgroundColor)
        return background(window.ignoresSafeArea())
            .presentationBackground(window)
    }
}

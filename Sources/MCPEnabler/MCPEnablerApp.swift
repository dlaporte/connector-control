import SwiftUI
import MCPEnablerCore

@main
struct MCPEnablerApp: App {
    @StateObject private var state = AppState()

    var body: some Scene {
        MenuBarExtra {
            PopoverView()
                .environmentObject(state)
        } label: {
            // puzzlepiece: distinctive next to system items — switch.2 was
            // nearly identical to the Control Center icon.
            Image(systemName: state.missingEnabled.isEmpty
                ? "puzzlepiece.extension.fill" : "exclamationmark.triangle.fill")
        }
        .menuBarExtraStyle(.window)
    }
}

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
            Image(systemName: state.missingEnabled.isEmpty
                ? "switch.2" : "exclamationmark.triangle.fill")
        }
        .menuBarExtraStyle(.window)
    }
}

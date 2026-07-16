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
            // A distinctive glyph matters here: switch.2 was nearly identical
            // to the Control Center icon.
            Image(systemName: state.missingEnabled.isEmpty
                ? "powerplug.fill" : "exclamationmark.triangle.fill")
        }
        .menuBarExtraStyle(.window)

        WindowGroup("Connector Editor", id: "editor", for: EditTarget.self) { $target in
            if let target = $target.wrappedValue {
                EditSheetView(target: target)
                    .environmentObject(state)
                    .navigationTitle(target.isNew ? "Add Connector" : "Edit “\(target.name)”")
            } else {
                Text("Choose a connector from the menu bar popover.")
                    .foregroundStyle(.secondary)
                    .padding(40)
            }
        }
        .windowResizability(.contentSize)

        Settings {
            SettingsView()
                .environmentObject(state)
        }
    }
}

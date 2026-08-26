import SwiftUI
import ConnectorControlCore

@main
struct ConnectorControlApp: App {
    @StateObject private var state = AppState()

    var body: some Scene {
        MenuBarExtra {
            PopoverView()
                .environmentObject(state)
        } label: {
            // A distinctive glyph matters here: switch.2 was nearly identical
            // to the Control Center icon. The alarm variant marks the one
            // persistent problem state: a failed apply awaiting retry.
            Image(systemName: state.applyRetryNeeded
                ? "exclamationmark.triangle.fill" : "powerplug.fill")
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
        .windowResizability(.contentMinSize)

        Settings {
            SettingsView()
                .environmentObject(state)
        }
    }
}

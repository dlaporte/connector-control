import SwiftUI
import ConnectorControlState

@main
struct ConnectorControlApp: App {
    private let services = LiveServices.shared
    @StateObject private var state = LiveServices.shared.state

    var body: some Scene {
        MenuBarExtra {
            PopoverView(state: state)
        } label: {
            // A distinctive glyph matters here: switch.2 was nearly identical
            // to the Control Center icon. The alarm variant marks the one
            // persistent problem state: a failed apply awaiting retry.
            Image(systemName: state.applyRetryNeeded
                ? "exclamationmark.triangle.fill" : "powerplug.fill")
        }
        .menuBarExtraStyle(.window)

        WindowGroup("Connector Editor", id: EditTarget.editorWindowID, for: EditTarget.self) { $target in
            if let target = $target.wrappedValue {
                EditSheetView(state: state, target: target)
                    .navigationTitle(target.windowTitle)
            } else {
                Text("Choose a connector from the menu bar popover.")
                    .foregroundStyle(.secondary)
                    .padding(40)
            }
        }
        .windowResizability(.contentMinSize)

        Settings {
            SettingsView(state: state, settings: services.settings,
                         autostart: services.autostart, updater: services.updater)
        }
    }
}

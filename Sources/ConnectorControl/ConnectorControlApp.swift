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

        WindowGroup(EditorModel.windowGroupTitle, id: EditTarget.editorWindowID, for: EditTarget.self) { $target in
            if let target = $target.wrappedValue {
                EditorWindowView(state: state, target: target)
                    .navigationTitle(target.windowTitle)
            } else {
                NoTargetWindow()
            }
        }
        .windowResizability(.contentMinSize)

        // A Window, not a WindowGroup: openWindow(id:) on a group opens a second copy, and two
        // Collections windows would hold two models racing to consume one request. One unique
        // window, re-activated when it is already open, is what the Windows registry keeps too.
        // Which collection it shows is its own selection; what the popover wants of it travels
        // through AppState.
        Window(CollectionsModel.windowTitle, id: CollectionsWindowView.windowID) {
            CollectionsWindowView(state: state)
        }
        // One surface, with no title-bar strip: the sidebar's own "Collections" header names the
        // window, so the bar would only repeat it, in a band of another colour. The title still
        // names the window in the Window menu, Mission Control and the Dock, and the transparent
        // bar still drags the window. This scene only: the editor and Settings keep their bars.
        // Windows keeps its native title bar, which is the platform's idiom there.
        .windowStyle(.hiddenTitleBar)
        .defaultSize(width: 760, height: 520)
        .windowResizability(.contentMinSize)

        Settings {
            SettingsView(state: state, settings: services.settings,
                         autostart: services.autostart, updater: services.updater)
        }
    }
}

/// An editor window with no connector to show: one SwiftUI restored from an encoding this version
/// cannot decode, such as a 1.3 window left open over an upgrade, whose target named no
/// collection. It closes itself rather than stand empty; the message is on screen only until it
/// does. It never falls back to the active collection, which would edit a collection the window
/// was never opened on. Windows reopens no editors at launch, so has no such window.
private struct NoTargetWindow: View {
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        Text(EditorModel.noTargetMessage)
            .foregroundStyle(.secondary)
            .padding(40)
            // A turn later, once the restored window is on screen and can be closed.
            .onAppear { DispatchQueue.main.async { dismiss() } }
    }
}
